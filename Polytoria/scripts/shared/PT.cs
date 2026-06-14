// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Scripting;
#if CREATOR
using Polytoria.Creator.UI;
#endif
using System;
using System.Text;

namespace Polytoria.Shared;

public static class PT
{
	public static int OwnerThreadId { get; private set; }

	static PT()
	{
		OwnerThreadId = System.Environment.CurrentManagedThreadId;
	}
	
	/// <summary>
	/// Joins a params array into one single string without creating any extra
	/// allocations for the single-element case
	/// </summary>
	private static string BuildMessage(object?[] parts)
	{
		if (parts.Length == 1)
			return parts[0]?.ToString() ?? string.Empty;
			
		// StringBuilder avoids O(n²) string allocations
		StringBuilder sb = new();
		foreach (object? part in parts)
			sb.Append(part);
		return sb.ToString();
	}
	
	/// <summary>
	/// Raw print to GD or console fallback
	/// </summary>
	private static void WriteOutput(string message, LogDispatcher.LogTypeEnum logType)
	{
		if (Globals.GDAvailable)
		{
			switch (logType)
			{
				case LogDispatcher.LogTypeEnum.Warning:
					GD.PrintRich($"[color=yellow][WARN] {message}[/color]");
					break;
				case LogDispatcher.LogTypeEnum.Error:
					GD.PrintRich($"[color=red][ERROR] {message}[/color]");
					GD.PushError(message);
					break;
				default:
					GD.Print(message);
					break;
			}
		}
		else
		{
			string prefix = logType switch
			{
				LogDispatcher.LogTypeEnum.Warning => "[WARN] ",
				LogDispatcher.LogTypeEnum.Error   => "[ERROR] ",
				_                                 => "[WARN] ",
			};
			Console.WriteLine(prefix + message);
		}
	}

	/// <summary>Single argument fast path with no array allocation.</summary>
	public static void Print(string message)
	{
		WriteOutput(message, LogDispatcher.LogTypeEnum.Info);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Info });
	}
	
	public static void Print(params object?[] str)
	{
		string message = BuildMessage(str);
		WriteOutput(message, LogDispatcher.LogTypeEnum.Info);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Info });
	}

	/// <summary>
	/// Verbose printing with no log dispatch
	/// The V suffix intentionally skips DispatchLog
	/// </summary>
	public static void PrintV(string message)
		=> WriteOutput(message, LogDispatcher.LogTypeEnum.Info);
		
	public static void PrintV(params object?[] str)
		=> WriteOutput(BuildMessage(str), LogDispatcher.LogTypeEnum.Info);

	public static void PrintWarn(string message)
	{
		WriteOutput(message, LogDispatcher.LogTypeEnum.Warning);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Warning });
	}
	
	public static void PrintWarn(params object?[] str)
	{
		string message = BuildMessage(str);
		WriteOutput(message, LogDispatcher.LogTypeEnum.Warning);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Warning });
	}
	
	public static void PrintErr(string message)
	{
		WriteOutput(message, LogDispatcher.LogTypeEnum.Error);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Error });
	}

	public static void PrintErr(params object?[] str)
	{
		string message = BuildMessage(str);
		WriteOutput(message, LogDispatcher.LogTypeEnum.Error);
		DispatchLog(new() { Content = message, LogType = LogDispatcher.LogTypeEnum.Error });
	}

	/// <summary>
	/// Print error verbose with no log dispatch
	/// </summary>
	/// <param name="str"></param>
	public static void PrintErrV(string message)
		=> WriteOutput(message, LogDispatcher.LogTypeEnum.Error);
	
	public static void PrintErrV(params object?[] str)
		=> WriteOutput(BuildMessage(str), LogDispatcher.LogTypeEnum.Error);

	public static void DispatchLog(LogDispatcher.LogData data)
	{
		try
		{
#if CREATOR
			// TODO: Turn this into an event instead? (Yes)
			CallOnMainThread(() =>
			{
				DebugConsole.Singleton?.NewLog(data);
			});
#endif
			if (data.LogType != LogDispatcher.LogTypeEnum.Info)
				World.Current?.ScriptService?.Logger.DispatchLog(data);
		}
		catch (Exception ex)
		{
			// Failed to dispatch log
			WriteOutput($"[Log Dispatch] {ex}", LogDispatcher.LogTypeEnum.Error);
		}
	}

	public static bool IsMainThread()
		=> System.Environment.CurrentManagedThreadId == OwnerThreadId;
	
	public static void CallOnMainThread(Action a)
	{
		if (IsMainThread() || !Globals.GDAvailable)
		{
			a();
		}
		else
		{
			Callable.From(a).CallDeferred();
		}
	}

	public static void CallDeferred(Action a)
	{
		if (!Globals.GDAvailable)
		{
			a();
		}
		else
		{
			Callable.From(a).CallDeferred();
		}
	}
}
