﻿using Microsoft.Extensions.Logging;
using Nethereum.JsonRpc.Client;
using Newtonsoft.Json;
using Nostr.Client.Client;
using Nostr.Client.Communicator;
using Nostr.Client.Responses;
using System.Net.WebSockets;
using Websocket.Client;

namespace PLang.Modules.MessageModule
{
	public class NostrClientManager : IDisposable
	{
		private NostrMultiWebsocketClient _client = null;
		private bool _disposed;

		public NostrMultiWebsocketClient GetClient(List<string> relayUrls)
		{
			if (_client != null) return _client;


			NostrWebsocketCommunicator[] relays = new NostrWebsocketCommunicator[relayUrls.Count];
			for (int i = 0; i < relayUrls.Count; i++)
			{
				relays[i] = new NostrWebsocketCommunicator(new Uri(relayUrls[i]));
			}

			var communicators = CreateCommunicators(relayUrls);
			ILogger<NostrWebsocketClient> nostrLogger = new Services.LoggerService.Logger<NostrWebsocketClient>();
			
			_client?.Dispose();
			_client = new NostrMultiWebsocketClient(nostrLogger, communicators.ToArray());
			communicators.ForEach(x => x.Start());

			return _client;

		}

		public virtual void Dispose()
		{
			if (_disposed)
			{
				return;
			}
			_client.Dispose();
			_disposed = true;
		}

		protected virtual void ThrowIfDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(GetType().FullName);
			}
		}

		private List<NostrWebsocketCommunicator> CreateCommunicators(List<string> relays)
		{
			var communicators = new List<NostrWebsocketCommunicator>();
			relays.ForEach(relay => communicators.Add(CreateCommunicator(new Uri(relay))));
			return communicators;
		}

		private NostrWebsocketCommunicator CreateCommunicator(Uri uri)
		{
			var comm = new NostrWebsocketCommunicator(uri, () =>
			{
				var client = new ClientWebSocket();
				client.Options.SetRequestHeader("Origin", "http://localhost");
				return client;
			});

			SetCommunicatorParam(comm, uri);

			return comm;
		}

		private void SetCommunicatorParam(NostrWebsocketCommunicator comm, Uri uri)
		{
			comm.Name = uri.Host;
			
		}
	}
}
