// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
#if !USE_NATIVE_HTTP
using System;
using System.Net;
using System.Threading;
#endif

namespace Polytoria.Shared;

public partial class PTHttpClient
{
	private const int DefaultDownloadChunkSize = 10000;

#if USE_NATIVE_HTTP
	// One shared HttpClient for the process lifetime
	// SocketsHttpHandler gives explicit control over connection pooling.
	private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(10),
		MaxConnectionsPerServer  = 20,
	});
#endif
	public Dictionary<string, string> DefaultRequestHeaders { get; set; } = [];

	public PTHttpClient()
	{
		DefaultRequestHeaders["User-Agent"] = $"Polytoria Client {Globals.AppVersion}";
	}

#if !USE_NATIVE_HTTP
	public Task<HttpResponseMessage> SendAsync(HttpRequestMessage msg,
		CancellationToken ct = default)
	{
		// Check nohttp feature flag
		if (Globals.UseNoHttp)
			throw new HttpRequestException("Http is disabled via feature flag");

		const int TypicalHttpHeaders = 8;
		int headerCount = DefaultRequestHeaders.Count + TypicalHttpHeaders;
		List<string> headers = new(headerCount);

		foreach (var (k, v) in DefaultRequestHeaders)
			headers.Add($"{k}: {v}");

		foreach (var item in msg.Headers)
			headers.Add($"{item.Key}: {string.Join(", ", item.Value)}");

		// Add content headers if present
		if (msg.Content != null)
			foreach (var item in msg.Content.Headers)
				headers.Add($"{item.Key}: {string.Join(", ", item.Value)}");

		string[] headersArray = [.. headers];

		TaskCompletionSource<HttpResponseMessage> tcs =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		// needs to be callable due to add_child
		Callable.From(() =>
		{
			// Exceptions inside CallDeferred must be explicitly forwarded to the TCS
			async Task RunAsync()
			{
				try
				{
					byte[] body = msg.Content != null
						? await msg.Content.ReadAsByteArrayAsync()
						: [];

					HttpRequest req = new() { DownloadChunkSize = DefaultDownloadChunkSize };

					Globals.Singleton.AddChild(req);

					req.RequestCompleted += (result, responseCode, responseHeaders, responseBody) =>
					{
						// Surface Godot level transport errors as exceptions rather than
						// letting callers see a 0 status response.
						if (result != (long)HttpRequest.Result.Success)
						{
							req.QueueFree();
							tcs.TrySetException(
								new HttpRequestException($"Godot HttpRequest failed: {(HttpRequest.Result)result}"));
							return;
						}

						HttpResponseMessage response = new((HttpStatusCode)responseCode)
						{
							Content = new ByteArrayContent(responseBody)
						};

						foreach (string header in responseHeaders)
						{
							int index = header.IndexOf(':');
							if (index > 0)
							{
								response.Headers.TryAddWithoutValidation(
									header[..index].Trim(),
									header[(index + 1)..].Trim()
								);
							}
						}

						req.QueueFree();
						tcs.SetResult(response);
					};

					ct.Register(() =>
					{
						req.CancelRequest();
						req.QueueFree();
						tcs.TrySetCanceled(ct);
					});

					Error error = req.RequestRaw(
						msg.RequestUri?.ToString()
							?? throw new InvalidOperationException("URL is null"),
						headersArray,
						Enum.Parse<Godot.HttpClient.Method>(
							msg.Method.Method.ToLower().Capitalize()),
						new ReadOnlySpan<byte>(body));

					if (error != Error.Ok)
						throw new HttpRequestException($"HttpRequest.RequestRaw error: {error}");
				}
				catch (Exception ex)
				{
					tcs.TrySetException(ex);
				}
			}

			_ = RunAsync();
		}).CallDeferred();

		return tcs.Task;
	}
#else
	public Task<HttpResponseMessage> SendAsync(HttpRequestMessage msg,
		CancellationToken ct = default)
	{
		foreach ((string key, string val) in DefaultRequestHeaders)
		{
			msg.Headers.TryAddWithoutValidation(key, val);
		}
		return _httpClient.SendAsync(msg, ct);
	}
#endif

	public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
	{
		// HttpRequestMessage ownership transfers to SendAsync
		var msg = new HttpRequestMessage(HttpMethod.Get, url);
		return SendAsync(msg, ct);
	}

	public async Task<T?> GetFromJsonAsync<T>(string url, JsonTypeInfo<T> jsonTypeInfo,
		CancellationToken ct = default)
	{
		var msg = new HttpRequestMessage(HttpMethod.Get, url);
		msg.Headers.TryAddWithoutValidation("Accept", "application/json");

		using HttpResponseMessage response = await SendAsync(msg, ct);
		response.EnsureSuccessStatusCode();

		string json = await response.Content.ReadAsStringAsync(ct);
		return JsonSerializer.Deserialize(json, jsonTypeInfo);
	}

	public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
	{
		using HttpResponseMessage response = await GetAsync(url, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsByteArrayAsync(ct);
	}

	public Task<HttpResponseMessage> PostAsync(string url, HttpContent content,
		CancellationToken ct = default)
	{
		var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

		return SendAsync(msg, ct);
	}

	public Task<HttpResponseMessage> PostAsJsonAsync<T>(string url, T value,
		JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct = default)
	{
		string json = JsonSerializer.Serialize(value, jsonTypeInfo);

		var msg = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json")
		};

		return SendAsync(msg, ct);
	}

	public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
	{
		using HttpResponseMessage response = await GetAsync(url, ct);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadAsStringAsync(ct);
	}
}
