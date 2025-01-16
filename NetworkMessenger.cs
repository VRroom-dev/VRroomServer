using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using JetBrains.Annotations;

[PublicAPI]
public static class NetworkMessenger {
	private static readonly Socket UdpSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
	private static readonly ConcurrentDictionary<EndPoint, Peer> Peers = new();
	private static readonly ConcurrentQueue<byte[]> ReceivedQueue = new();
	private static readonly byte[] Buffer = new byte[65507];
	private const byte Version = 0; // 2 bit number, only 0, 1, 2, 3 are valid
	public static event Action<EndPoint>? OnMessageReceived;
	public static event Action<EndPoint>? OnPeerTimout;
	public static float PeerTimeout = 60;
	
	public static void Start(int port) {
		UdpSocket.Bind(new IPEndPoint(IPAddress.Any, port));
		new Thread(Receive).Start();
	}
	
	public static void Update() {
		foreach (Peer peer in Peers.Values) peer.Update();
	}

	public static bool TryDequeue(out byte[] msg) => ReceivedQueue.TryDequeue(out msg!);

	public static void Send(byte[] data, byte messageType, byte channel, EndPoint endPoint) {
		byte[] msg = new byte[data.Length + 3];
		msg[0] = (byte)(Version | (messageType << 2));
		msg[1] = channel;
		msg[2] = Peers.GetOrAdd(endPoint, ep => new Peer(ep)).GetNextSequenceNumber(channel);
		System.Buffer.BlockCopy(data, 0, msg, 3, data.Length);
		Send(msg, endPoint);
	}

	private static void Send(byte[] msg, EndPoint endPoint) {
		UdpSocket.SendTo(msg, endPoint);
		Peers[endPoint].UpdateLastActive();
	}

	private static void Receive() {
		EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
		while (true) {
			try {
				int bytes = UdpSocket.ReceiveFrom(Buffer, ref remoteEp);
				if ((Buffer[0] & 3) != Version) continue;
				if (bytes < 3) continue;

				int messageType = (Buffer[0] >> 2) & 7;
				bool isAck = ((Buffer[0] >> 5) & 1) == 1;
				byte channel = Buffer[1];
				byte sequence = Buffer[2];

				Peer peer = Peers.GetOrAdd(remoteEp, ep => new Peer(ep));
				peer.UpdateLastActive();

				if (isAck) {
					peer.HandleAck(channel, sequence);
				} else if (bytes > 3) {
					byte[] data = new byte[bytes - 3];
					System.Buffer.BlockCopy(Buffer, 3, data, 0, bytes - 3);
					switch (messageType) {
						case 0:
							ReceivedQueue.Enqueue(data);
							break;
						case 1:
							peer.HandleUnreliableSequenced(channel, sequence, data);
							break;
						case 2:
							peer.HandleReliable(channel, sequence, data);
							break;
						case 3:
							peer.HandleReliableSequenced(channel, sequence, data);
							break;
						case 4:
							peer.HandleReliableOrdered(channel, sequence, data);
							break;
					}
					OnMessageReceived?.Invoke(remoteEp);
				}
			} catch (Exception e) {
				Console.WriteLine(e);
			}
		}
	}
	
	private class Peer(EndPoint endPoint) {
		private readonly Dictionary<byte, byte> _outgoingSequenceNumbers = new();
		private readonly Dictionary<byte, byte> _incomingSequenceNumbers = new();
		private readonly Dictionary<byte, SortedDictionary<byte, byte[]>> _orderedMessages = new();
		private readonly ConcurrentDictionary<(byte channel, byte sequence), AckMessage> _unAckedMessages = new();
		private DateTime _lastActive = DateTime.UtcNow;
		
		private record AckMessage(byte[] Data, DateTime LastSent, byte Retries);

		// ReSharper disable once MemberHidesStaticFromOuterClass
		public void Update() {
			DateTime now = DateTime.UtcNow;
			foreach (var (key, ackMessage) in _unAckedMessages) {
				if ((now - ackMessage.LastSent).TotalSeconds < 1) continue;
				if (ackMessage.Retries > 5) _unAckedMessages.Remove(key, out _);
				
				Send(ackMessage.Data, endPoint);
				_unAckedMessages[key] = new(ackMessage.Data, now, (byte)(ackMessage.Retries + 1));
			}

			if (!((now - _lastActive).TotalSeconds > PeerTimeout)) return;
			Peers.TryRemove(endPoint, out _);
			OnPeerTimout?.Invoke(endPoint);
		}

		public void UpdateLastActive() => _lastActive = DateTime.UtcNow;

		public void HandleAck(byte channel, byte sequence) {
			_unAckedMessages.Remove((channel, sequence), out _);
		}

		public void HandleUnreliableSequenced(byte channel, byte sequence, byte[] data) {
			if (!IsNewerSequenceNumber(channel, sequence)) return;
			_incomingSequenceNumbers[channel] = sequence;
			ReceivedQueue.Enqueue(data);
		}

		public void HandleReliable(byte channel, byte sequence, byte[] data) {
			SendAck(channel, sequence);
			ReceivedQueue.Enqueue(data);
		}

		public void HandleReliableSequenced(byte channel, byte sequence, byte[] data) {
			SendAck(channel, sequence);
			if (!IsNewerSequenceNumber(channel, sequence)) return;
			_incomingSequenceNumbers[channel] = sequence;
			ReceivedQueue.Enqueue(data);
		}

		public void HandleReliableOrdered(byte channel, byte sequence, byte[] data) {
			SendAck(channel, sequence);
			if (!_orderedMessages.TryGetValue(channel, out var channelMessages)) {
				channelMessages = new SortedDictionary<byte, byte[]>();
				_orderedMessages[channel] = channelMessages;
			}

			channelMessages[sequence] = data;

			while (channelMessages.Count > 0) {
				byte firstSeqNum = channelMessages.Keys.Min();
				if (!IsNextSequenceNumber(channel, firstSeqNum)) break;
				_incomingSequenceNumbers[channel] = firstSeqNum;
				ReceivedQueue.Enqueue(channelMessages[firstSeqNum]);
				channelMessages.Remove(firstSeqNum);
			}
		}
		
		public byte GetNextSequenceNumber(byte channel) {
			if (!_outgoingSequenceNumbers.TryGetValue(channel, out byte seqNum)) seqNum = 0;
			_outgoingSequenceNumbers[channel] = (byte)((seqNum + 1) % 256);
			return seqNum;
		}
		
		private void SendAck(byte channel, byte sequence) {
			byte[] msg = new byte[3];
			msg[0] = Version | 40;
			msg[1] = channel;
			msg[2] = sequence;
			Send(msg, endPoint);
		}

		private bool IsNewerSequenceNumber(byte channel, byte seqNum) {
			if (!_incomingSequenceNumbers.TryGetValue(channel, out byte lastSeqNum)) return true;
			return (byte)((seqNum - lastSeqNum + 256) % 256) < 128;
		}

		private bool IsNextSequenceNumber(byte channel, byte seqNum) {
			if (!_incomingSequenceNumbers.TryGetValue(channel, out byte lastSeqNum)) return true;
			return seqNum == (byte)((lastSeqNum + 1) % 256);
		}
	}
}