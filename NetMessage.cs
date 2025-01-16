using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;

[PublicAPI]
public class NetMessage {
	private byte[] _buffer;
	private int _position;
	private int _length;
	private byte _currentByte;
	private int _bitPosition;

	public byte[] Data => _buffer;

	public NetMessage(int initialCapacity = 1024) {
		_buffer = new byte[initialCapacity];
		_position = 0;
		_length = 0;
		_currentByte = 0;
		_bitPosition = 0;
	}

	public NetMessage(byte[] data) {
		_buffer = data;
		_position = 0;
		_length = data.Length;
		_currentByte = 0;
		_bitPosition = 0;
	}

	private void EnsureCapacity(int additionalBytes) {
		if (_position + additionalBytes <= _buffer.Length) return;
		int newSize = Math.Max(_buffer.Length * 2, _position + additionalBytes);
		byte[] newBuffer = new byte[newSize];
		Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
		_buffer = newBuffer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void FlushBits() {
		if (_bitPosition <= 0) return;
		_buffer[_position++] = _currentByte;
		_currentByte = 0;
		_bitPosition = 0;
	}

	public void WriteBool(bool value) {
		if (_bitPosition == 8) FlushBits();
		if (value) _currentByte |= (byte)(1 << _bitPosition);
		_bitPosition++;
		_length = Math.Max(_length, _position + (_bitPosition > 0 ? 1 : 0));
	}

	public void WriteByte(byte value) {
		FlushBits();
		EnsureCapacity(sizeof(byte));
		_buffer[_position] = value;
		_position += sizeof(byte);
		_length = Math.Max(_length, _position);
	}

	public void WriteShort(short value) {
		FlushBits();
		EnsureCapacity(sizeof(short));
		BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
		_position += sizeof(short);
		_length = Math.Max(_length, _position);
	}

	public void WriteInt(int value) {
		FlushBits();
		EnsureCapacity(sizeof(int));
		BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
		_position += sizeof(int);
		_length = Math.Max(_length, _position);
	}

	public void WriteLong(long value) {
		FlushBits();
		EnsureCapacity(sizeof(long));
		BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
		_position += sizeof(long);
		_length = Math.Max(_length, _position);
	}

	public void WriteFloat(float value) {
		FlushBits();
		EnsureCapacity(sizeof(float));
		uint intValue = unchecked((uint)BitConverter.SingleToInt32Bits(value));
		BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), intValue);
		_position += sizeof(float);
		_length = Math.Max(_length, _position);
	}

	public void WriteDouble(double value) {
		FlushBits();
		EnsureCapacity(sizeof(double));
		ulong longValue = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
		BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), longValue);
		_position += sizeof(double);
		_length = Math.Max(_length, _position);
	}

	public void WriteString(string value) {
		FlushBits();
		byte[] stringBytes = Encoding.UTF8.GetBytes(value);
		WriteInt(stringBytes.Length);
		EnsureCapacity(stringBytes.Length);
		Buffer.BlockCopy(stringBytes, 0, _buffer, _position, stringBytes.Length);
		_position += stringBytes.Length;
		_length = Math.Max(_length, _position);
	}

	public void WriteBytes(byte[] value, bool writeLength = false) {
		FlushBits();
		if (writeLength) WriteInt(value.Length);
		EnsureCapacity(value.Length);
		Buffer.BlockCopy(value, 0, _buffer, _position, value.Length);
		_position += value.Length;
		_length = Math.Max(_length, _position);
	}

	public bool ReadBool() {
		if (_bitPosition == 0) _currentByte = _buffer[_position++];
		bool value = (_currentByte & (1 << _bitPosition)) != 0;
		_bitPosition = (_bitPosition + 1) % 8;
		return value;
	}

	public byte ReadByte() {
		FlushBits();
		byte value = _buffer[_position];
		_position += sizeof(byte);
		return value;
	}

	public short ReadShort() {
		FlushBits();
		short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(_position));
		_position += sizeof(short);
		return value;
	}

	public int ReadInt() {
		FlushBits();
		int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position));
		_position += sizeof(int);
		return value;
	}

	public long ReadLong() {
		FlushBits();
		long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position));
		_position += sizeof(long);
		return value;
	}

	public float ReadFloat() {
		FlushBits();
		uint intValue = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position));
		_position += sizeof(float);
		return BitConverter.Int32BitsToSingle(unchecked((int)intValue));
	}

	public double ReadDouble() {
		FlushBits();
		ulong longValue = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_position));
		_position += sizeof(double);
		return BitConverter.Int64BitsToDouble(unchecked((long)longValue));
	}

	public string ReadString() {
		FlushBits();
		int length = ReadInt();
		string value = Encoding.UTF8.GetString(_buffer, _position, length);
		_position += length;
		return value;
	}

	public byte[] ReadBytes(int length = 0) {
		FlushBits();
		if (length == 0) length = ReadInt();
		byte[] value = new byte[length];
		Buffer.BlockCopy(_buffer, _position, value, 0, length);
		_position += length;
		return value;
	}

	public byte[] ToBytes() {
		FlushBits();
		byte[] result = new byte[_length];
		Buffer.BlockCopy(_buffer, 0, result, 0, _length);
		return result;
	}
}