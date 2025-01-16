using System.Net;
using System.Text.Json;

namespace VRroomServer;
internal static class Program {
	private static readonly HttpClient HttpClient = new() { BaseAddress = new("https://api.koneko.cat/") };
	private static readonly Dictionary<EndPoint, Client> Clients = new();
	private static short _currentNetworkId;
	
	private static void Main(string[] args) {
		NetworkMessenger.Start(args.Length > 0 ? int.Parse(args[0]) : 31130);
		NetworkMessenger.OnMessageReceived += ReceiveMessage;
		NetworkMessenger.OnPeerTimout += HandleDisconnect;

		DateTime sinceLastPlayer = DateTime.Now;
		while (true) {
			//if (Clients.Count > 0) sinceLastPlayer = DateTime.Now;
			//if ((DateTime.Now - sinceLastPlayer).TotalMinutes > 1) Environment.Exit(0);
			NetworkMessenger.Update();
			BroadcastPlayerUpdates();
			Thread.Sleep(1);
		}
	}

	private static void BroadcastPlayerUpdates() {
		foreach (Client sender in Clients.Values) {
			if (sender.LastPositionData == null && sender.LastSkeletalData == null) continue;
        
			foreach ((EndPoint endPoint, Client receiver) in Clients) {
				if (sender.NetworkId == receiver.NetworkId) continue;
            
				if (!receiver.UpdateRate.TryGetValue(sender.NetworkId, out var receiverRate) || 
					!sender.UpdateRate.TryGetValue(receiver.NetworkId, out var senderRate)) continue;
            
				byte effectiveRate = Math.Min(receiverRate.updateRate, senderRate.updateRate);
            
				double updateInterval = 1.0 / effectiveRate;
				if ((DateTime.Now - receiverRate.lastUpdate).TotalSeconds < updateInterval) continue;
            
				if (sender.LastPositionData != null) {
					NetMessage msg = new();
					msg.WriteShort(4);
					msg.WriteShort(sender.NetworkId);
					msg.WriteByte(effectiveRate);
					msg.WriteBytes(sender.LastPositionData[sizeof(short)..]);
					NetworkMessenger.Send(msg.ToBytes(), 1, 64, endPoint);
				}
            
				if (sender.LastSkeletalData != null) {
					NetMessage msg = new();
					msg.WriteShort(5);
					msg.WriteShort(sender.NetworkId);
					msg.WriteByte(effectiveRate);
					msg.WriteBytes(sender.LastSkeletalData[sizeof(short)..]);
					NetworkMessenger.Send(msg.ToBytes(), 1, 64, endPoint);
				}
            
				receiver.UpdateRate[sender.NetworkId] = (receiverRate.updateRate, DateTime.Now);
			}
		}
	}

	private static void HandleDisconnect(EndPoint ep) {
		Clients.Remove(ep, out Client? client);
		if (client == null) return;
		NetMessage msg = new();
		msg.WriteShort(201);
		msg.WriteShort(client.NetworkId);
		Broadcast(msg, ep);
	}

	private static void ReceiveMessage(EndPoint ep) {
		if (!NetworkMessenger.TryDequeue(out byte[] data)) return;
		NetMessage msg = new(data);
		try {
			switch (msg.ReadShort()) {
				case 0: HandleJoinRequest(msg, ep); break;
				case 1: HandleDisconnect(ep); break;
				case 2: HandleClientState(msg, ep); break;
				case 3: HandleVoiceData(msg, ep); break;
				case 4: HandlePositionData(msg, ep); break;
				case 5: HandleSkeletalData(msg, ep); break;
				default: Broadcast(msg, ep); break;
			}
		} catch (Exception e) {
			Console.WriteLine(e);
		}
	}

	private static void HandleSkeletalData(NetMessage msg, EndPoint ep) {
		if (!Clients.TryGetValue(ep, out Client? sender)) return;
		sender.LastSkeletalData = msg.Data;
	}

	private static void HandlePositionData(NetMessage msg, EndPoint ep) {
		if (!Clients.TryGetValue(ep, out Client? sender)) return;
		sender.LastPositionData = msg.Data;
	}

	private static void HandleVoiceData(NetMessage msg, EndPoint ep) {
		if (!Clients.TryGetValue(ep, out Client? sender)) return;
    
		foreach ((EndPoint endPoint, Client client) in Clients) {
			if (endPoint == ep) continue;
			if (client.CanHear.Contains(sender.NetworkId) && Clients[ep].CanHear.Contains(client.NetworkId)) {
				NetworkMessenger.Send(msg.ToBytes(), 3, 0, endPoint);
			}
		}
	}

	private static async void HandleJoinRequest(NetMessage msg, EndPoint ep) {
		string userid = msg.ReadString();
		string token = msg.ReadString();
		
		StringContent content = new(JsonSerializer.Serialize(new { userid, token }), System.Text.Encoding.UTF8, "application/json");
		HttpResponseMessage response = await HttpClient.PostAsync("auth/join-token", content);
		JsonElement result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStreamAsync());
		if (!result.GetProperty("valid").GetBoolean()) return;

		Client client = new(_currentNetworkId++, Guid.Parse(userid));
		NetMessage outMsg = new();
		outMsg.WriteShort(200);
		outMsg.WriteShort(client.NetworkId);
		outMsg.WriteString(client.UserId.ToString());
		Broadcast(outMsg, ep);

		outMsg = new();
		outMsg.WriteShort(202);
		outMsg.WriteShort((short)Clients.Count);
		foreach (Client value in Clients.Values) {
			outMsg.WriteShort(value.NetworkId);
			outMsg.WriteString(value.UserId.ToString());
		}
		NetworkMessenger.Send(outMsg.ToBytes(), 2, 0, ep);
		Clients[ep] = client;
	}

	private static void HandleClientState(NetMessage msg, EndPoint ep) {
		if (!Clients.TryGetValue(ep, out Client? sender)) return;
		sender.UpdateRate.Clear();
		sender.CanHear.Clear();
		
		short count = msg.ReadShort();
		for (int i = 0; i < count; i++) {
			short id = msg.ReadShort();
			byte data = msg.ReadByte();
			sender.UpdateRate[id] = ((byte)Math.Clamp(data & 0x7F, 1, 60), DateTime.UnixEpoch);
			if ((data & 0x80) != 0) sender.CanHear.Add(id);
		}
	}

	private static void Broadcast(NetMessage msg, EndPoint? exclude = null) {
		byte[] bytes = msg.ToBytes();
		foreach (EndPoint endPoint in Clients.Keys) {
			if (endPoint != exclude) NetworkMessenger.Send(bytes, 2, 0, endPoint);
		}
	}
}

public class Client(short networkId, Guid userId) {
	public readonly short NetworkId = networkId;
	public readonly Guid UserId = userId;
	public readonly HashSet<short> CanHear = [];
	public readonly Dictionary<short, (byte updateRate, DateTime lastUpdate)> UpdateRate = new();
	public byte[]? LastPositionData;
	public byte[]? LastSkeletalData;
}