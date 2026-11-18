using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EndpointToolbelt.Protocol;

/// <summary>
/// Messages are UTF-8 JSON, each preceded by a 4-byte little-endian length.
/// The reader enforces a maximum size before allocating the buffer.
/// </summary>
public static class MessageFraming
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Serialize<T>(T message)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var ms = new MemoryStream();
        serializer.WriteObject(ms, message);
        return ms.ToArray();
    }

    public static T Deserialize<T>(byte[] json)
    {
        // Validate UTF-8 strictly before parsing.
        _ = Utf8.GetString(json);
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var ms = new MemoryStream(json);
        return (T)serializer.ReadObject(ms);
    }

    /// <summary>Length header plus JSON body, ready to write in one call.</summary>
    public static byte[] Frame<T>(T message)
    {
        var body = Serialize(message);
        var header = BitConverter.GetBytes(body.Length);
        if (!BitConverter.IsLittleEndian) { Array.Reverse(header); }
        var frame = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
        return frame;
    }

    public static void Write<T>(Stream stream, T message)
    {
        var frame = Frame(message);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    /// <summary>Reads one message. Returns default when the stream ended cleanly before a header.</summary>
    public static T Read<T>(Stream stream, int maxBytes)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header, allowEndBeforeFirstByte: true)) { return default; }
        if (!BitConverter.IsLittleEndian) { Array.Reverse(header); }
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > maxBytes)
        {
            throw new InvalidDataException($"Message length {length} is outside 1..{maxBytes}.");
        }
        var body = new byte[length];
        ReadExactly(stream, body, allowEndBeforeFirstByte: false);
        return Deserialize<T>(body);
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, bool allowEndBeforeFirstByte)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                if (offset == 0 && allowEndBeforeFirstByte) { return false; }
                throw new EndOfStreamException("The connection closed in the middle of a message.");
            }
            offset += read;
        }
        return true;
    }
}
