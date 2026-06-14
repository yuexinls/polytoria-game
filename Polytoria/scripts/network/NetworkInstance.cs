// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Networking;

/// <summary>
/// ENet network instance
/// </summary>
public class NetworkInstance
{
	private const float SilenceTimeoutSeconds = 5.0f;
	private const int DataChannelAuthTimeoutMs = 10000;
	private const ENetConnection.CompressionMode CompressionMode = ENetConnection.CompressionMode.Zlib;
	private const int BandwidthInLimit = 0;
	private const int BandwidthOutLimit = 30 * 1024;
	private const int BandwidthPerPlayer = 200 * 1024; // 200 KB/s per player
	private const int DefaultCapacity = 67;
	private const int DefaultPort = 24221;
	private const int MinimumTimeout = 5;
	private const int ErrorBackoffMs = 100; // cooldown after a loop exception

	// They're written by the network thread, and read by the main thread, so they must be volatile.
	private volatile bool _isSilence = false;
	private volatile bool _shutdown = false;

	private long _lastMessageTicks;

	// It's only accessed from the main thread (DrainEvents + VerifyDataServerToken)
	// If VerifyDataServerToken is ever called off-thread, then we'd switch to ConcurrentDictionary
	private readonly Dictionary<int, string> _dataServerTokens = [];

	private readonly ENetConnection _peer;
	private readonly ConcurrentQueue<Action> _actionQueue = new();
	private readonly ConcurrentQueue<DeferredNetworkEvent> _mainThreadEventQueue = new();

	internal readonly ConcurrentDictionary<int, ENetPacketPeer> IdToPeer = [];
	internal readonly ConcurrentDictionary<ENetPacketPeer, int> PeerToId = [];

	private int _mainThreadDrainScheduled = 0;
	private int _peerCounter = 1;

	public ICollection<int> PeerIds => IdToPeer.Keys;

	/// <summary>True when no message has been received for SilenceTimeoutSeconds (client only)</summary>
	public bool IsSilence => _isSilence;

	public bool IsServer { get; private set; } = false;

	public event Action<int>? PeerConnected;
	public event Action<int>? PeerDisconnected;
	public event Action? ClientConnected;
	public event Action? ClientDisconnected;
	public event Action<NetInstanceErrorEnum>? ClientError;
	public event MessageReceivedHandler? MessageReceived;

	public NetworkInstance()
	{
		_peer = new();
	}

	public void CreateServer(int port = DefaultPort, int maxChannels = 3)
	{
		Error e = _peer.CreateHostBound("*", port, DefaultCapacity, maxChannels);
		_peer.Compress(CompressionMode);

		if (e != Error.Ok)
		{
			PT.PrintErr("Couldn't create host: ", e);
			return;
		}

		IsServer = true;
		StartNetworkLoop();
	}

	public Task CreateClient(string address, int port, int maxChannels = 3)
	{
		Error e = _peer.CreateHost(DefaultCapacity, maxChannels);
		_peer.BandwidthLimit(0, 0); // BandwidthInLimit, BandwidthOutLimit
		_peer.Compress(CompressionMode);

		if (e != Error.Ok)
		{
			PT.PrintErr("Couldn't create host: ", e);
			return Task.CompletedTask;
		}

		_peer.ConnectToHost(address, port);
		StartNetworkLoop();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Adapt server bandwidth to player count
	/// </summary>
	/// <param name="_">used to be player count</param>
	public void AdaptBandwidth(int _)
	{
		// TODO: TEMP FIX, unlimit out bandwidth (yes)
		_peer.BandwidthLimit(0, 0);
	}

	public void Shutdown()
	{
		if (_shutdown) return;
		_shutdown = true;

		foreach ((_, ENetPacketPeer pk) in IdToPeer)
			pk.PeerDisconnect();

		_peer.Flush();
		_peer.Destroy();
	}

	private void StartNetworkLoop()
	{
		_ = Task.Run(NetworkLoop);
	}

	// Background thread
	private void NetworkLoop()
	{
		Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
		
		while (!_shutdown)
		{
			if (!GodotObject.IsInstanceValid(_peer)) return;

			try
			{
				ProcessActionQueue();
				ProcessNetwork();
				CheckSilence();
				_peer.Flush();
			}
			catch (Exception ex)
			{
				GD.PushError("NetworkLoop exception: ", ex);

				// Backoff before retry; this prevents a bad-state exception from 
				// pinning the CPU at 100% with log spam
				if (!_shutdown)
					Thread.Sleep(ErrorBackoffMs);
			}
		}
	}

	internal bool VerifyDataServerToken(int peerID, string token)
	{
		// This must be called on the main thread; the same thread as DrainEvents
		if (_dataServerTokens.TryGetValue(peerID, out string? val) && val == token)
		{
			// DataServer Token success, remove the token too
			_dataServerTokens.Remove(peerID);
			return true;
		}
		return false;
	}

	public ENetPacketPeer? GetPacketPeerFromId(int id)
		=> IdToPeer.TryGetValue(id, out ENetPacketPeer? p) ? p : null;

	public bool IsPeerConnected(int peerID)
		=> IdToPeer.ContainsKey(peerID);

	public void SendMessage(int targetID, byte[] data, TransferMode transferMode, int transferChannel = 0)
	{
		_actionQueue.Enqueue(() =>
		{
			ENetPacketPeer? peer = GetPacketPeerFromId(targetID);
			if (peer == null)
			{
				GD.PushWarning(targetID, " doesn't exist");
				return;
			}
			Error err = peer.Send(transferChannel, data, (int)transferMode);
			if (err != Error.Ok)
			{
				GD.PushError("Send error: ", err);
			}
		});
	}

	public void DisconnectPeer(int targetID, bool force = false)
	{
		_actionQueue.Enqueue(() =>
		{
			ENetPacketPeer? peer = GetPacketPeerFromId(targetID);
			if (peer == null)
			{
				GD.PushWarning(targetID, " doesn't exist");
				return;
			}
			if (force)
			{
				peer.PeerDisconnectNow();
			}
			else
			{
				peer.PeerDisconnect();
			}
		});
	}

	/// <summary>
	/// Broadcast to all active peers (optionally skipping a set of peer IDs)
	/// It uses HashSet for O(1) exclusion lookup instead of an O(n) array scan
	/// </summary>
	public void BroadcastMessage(
		byte[] data,
		TransferMode transferMode,
		int transferChannel = 0,
		HashSet<int>? except = null)
	{
		_actionQueue.Enqueue(() =>
		{
			foreach ((int id, ENetPacketPeer peer) in IdToPeer)
			{
				if (!peer.IsActive()) continue;
				if (except != null && except.Contains(id)) continue;
				peer.Send(transferChannel, data, (int)transferMode);
			}
		});
	}

	public double PopStatistic(ENetConnection.HostStatistic hs)
		=> _peer.PopStatistic(hs);

	private void ProcessActionQueue()
	{
		while (_actionQueue.TryDequeue(out Action? action))
		{
			try
			{
				action.Invoke();
			}
			catch (Exception ex)
			{
				GD.PushError("Error processing queued action: ", ex);
			}
		}
	}

	private void ProcessNetwork()
	{
		Godot.Collections.Array serviceData = _peer.Service(MinimumTimeout);
		while (true)
		{
			ENetConnection.EventType eventType = (ENetConnection.EventType)(int)serviceData[0];
			if (eventType == ENetConnection.EventType.None)
				return; // break before allocating another Service() result

			ENetPacketPeer? fromPeer = (ENetPacketPeer?)serviceData[1];
			int peerID = 0;

			if (fromPeer != null)
				PeerToId.TryGetValue(fromPeer, out peerID);

			switch (eventType)
			{
				case ENetConnection.EventType.Connect:
					{
						if (fromPeer == null)
						{
							PT.PrintWarn("Connect received but peer is null, return");
							return;
						}

						peerID = IsServer ? Interlocked.Increment(ref _peerCounter) : 1;
						IdToPeer[peerID] = fromPeer;
						PeerToId[fromPeer] = peerID;

						EnqueueEvent(IsServer
							? new PeerConnectedEvent(peerID)
							: new ClientConnectedEvent());
						break;
					}
				case ENetConnection.EventType.Disconnect:
					{
						if (fromPeer == null)
						{
							PT.PrintWarn("Disconnect received but peer is null, return");
							return;
						}

						IdToPeer.TryRemove(peerID, out _);
						PeerToId.TryRemove(fromPeer, out _);

						EnqueueEvent(IsServer
							? new PeerDisconnectedEvent(peerID)
							: new ClientDisconnectedEvent());
						break;
					}
				case ENetConnection.EventType.Receive:
					{
						Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);

						if (fromPeer == null)
						{
							PT.PrintWarn("Message received but peer is null, return");
							return;
						}

						while (fromPeer.GetAvailablePacketCount() > 0)
						{
							int pkf = fromPeer.GetPacketFlags();
							TransferMode m = pkf switch
							{
								(int)ENetPacketPeer.FlagReliable => TransferMode.Reliable,
								(int)ENetPacketPeer.FlagUnreliableFragment => TransferMode.UnreliableOrdered,
								(int)ENetPacketPeer.FlagUnsequenced => TransferMode.Unreliable,
								_ => TransferMode.Unreliable,
							};
							byte[] data = fromPeer.GetPacket();
							EnqueueEvent(new MessageReceivedEvent(peerID, data, m));
						}
						break;
					}
				case ENetConnection.EventType.Error:
					PT.PrintErr("ENet connection error");
					EnqueueEvent(new ClientErrorEvent(NetInstanceErrorEnum.NetworkError));
					return;
			}

			serviceData = _peer.Service(0);
		}
	}

	private void CheckSilence()
	{
		// Only check silence in client
		if (IsServer) return;

		long lastTicks = Interlocked.Read(ref _lastMessageTicks);
		double elapsedSeconds = (DateTime.UtcNow.Ticks - lastTicks) / (double)TimeSpan.TicksPerSecond;
		bool currentlySilent = elapsedSeconds > SilenceTimeoutSeconds;

		if (currentlySilent == _isSilence) return;

		_isSilence = currentlySilent;

		if (_isSilence)
			PT.PrintErr("[!] Network connection has gone silent");
		else
			PT.Print("[i] Network connection resumed.");
	}

	private void EnqueueEvent(DeferredNetworkEvent e)
	{
		_mainThreadEventQueue.Enqueue(e);

		if (Interlocked.CompareExchange(ref _mainThreadDrainScheduled, 1, 0) == 0)
			Callable.From(DrainEvents).CallDeferred();
	}

	private void DrainEvents()
	{
		if (_shutdown)
		{
			_mainThreadEventQueue.Clear();
			Interlocked.Exchange(ref _mainThreadDrainScheduled, 0);
			return;
		}
		
		while (_mainThreadEventQueue.TryDequeue(out DeferredNetworkEvent? e))
		{
			switch (e)
			{
				case PeerConnectedEvent connected:
					// A single guid is 128 bits; it's enough collision resistance
					_dataServerTokens[connected.PeerID] = Guid.NewGuid().ToString("N");
					PeerConnected?.Invoke(connected.PeerID);
					break;

				case PeerDisconnectedEvent disconnected:
					_dataServerTokens.Remove(disconnected.PeerID);
					PeerDisconnected?.Invoke(disconnected.PeerID);
					break;

				case ClientConnectedEvent:
					ClientConnected?.Invoke();
					break;

				case ClientDisconnectedEvent:
					ClientDisconnected?.Invoke();
					break;

				case ClientErrorEvent error:
					ClientError?.Invoke(error.Error);
					break;

				case MessageReceivedEvent msg:
					MessageReceived?.Invoke(msg.PeerID, msg.Data, msg.TransferMode);
					break;
			}
		}
		Interlocked.Exchange(ref _mainThreadDrainScheduled, 0);

		if (!_mainThreadEventQueue.IsEmpty && Interlocked.CompareExchange(ref _mainThreadDrainScheduled, 1, 0) == 0)
		{
			Callable.From(DrainEvents).CallDeferred();
		}
	}

	public delegate void MessageReceivedHandler(int peerID, byte[] data, TransferMode transferMode);

	public enum NetInstanceErrorEnum
	{
		DataChannelConnectFailure,
		DataChannelAuthFailure,
		NetworkError
	}

	private abstract record DeferredNetworkEvent;
	private record PeerConnectedEvent(int PeerID) : DeferredNetworkEvent;
	private record PeerDisconnectedEvent(int PeerID) : DeferredNetworkEvent;
	private record ClientConnectedEvent : DeferredNetworkEvent;
	private record ClientDisconnectedEvent : DeferredNetworkEvent;
	private record ClientErrorEvent(NetInstanceErrorEnum Error) : DeferredNetworkEvent;
	private record MessageReceivedEvent(int PeerID, byte[] Data, TransferMode TransferMode) : DeferredNetworkEvent;
}

public enum AuthorityMode
{
	Server,
	Authority,
	Any
}

public enum TransferMode
{
	Reliable = (int)ENetPacketPeer.FlagReliable,
	UnreliableOrdered = (int)ENetPacketPeer.FlagUnreliableFragment,
	Unreliable = (int)ENetPacketPeer.FlagUnsequenced,
}

public class NetworkException(string err) : Exception(err) { }
