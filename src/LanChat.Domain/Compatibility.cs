namespace LanChat.Domain
{
    public static class AppIdentity
    {
        public static Guid NewId()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Guid's first three fields are little-endian in its byte representation.
            bytes[3] = (byte)(milliseconds >> 40);
            bytes[2] = (byte)(milliseconds >> 32);
            bytes[1] = (byte)(milliseconds >> 24);
            bytes[0] = (byte)(milliseconds >> 16);
            bytes[5] = (byte)(milliseconds >> 8);
            bytes[4] = (byte)milliseconds;
            bytes[7] = (byte)((bytes[7] & 0x0f) | 0x70);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
            return new Guid(bytes);
        }

        public static string NormalizeLines(this string value, string separator = "\r\n") =>
            value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", separator);
    }
}
