using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace UniSlop.MCP.Tests
{
    // Shared minimal HTTP/1.1 plumbing for the integration tests. HttpWebRequest is broken under
    // Unity's MonoBleedingEdge, so everything here speaks HTTP over a raw TcpClient, just like
    // Server.cs does at runtime.
    static class TestHttp
    {
        public static bool TryConnect(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect("127.0.0.1", port);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        // POSTs JSON and returns only the response body.
        public static string Post(int port, string path, string json)
        {
            string raw = Request(port, "POST", path, json);
            int bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return bodyStart >= 0 ? raw.Substring(bodyStart + 4) : raw;
        }

        // Sends one request and returns the full raw response (status line, headers and body).
        public static string Request(int port, string method, string path, string json)
        {
            byte[] body = json == null ? new byte[0] : Encoding.UTF8.GetBytes(json);
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                using (NetworkStream stream = client.GetStream())
                {
                    string head = method + " " + path + " HTTP/1.1\r\n"
                        + "Host: 127.0.0.1\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();

                    using (var received = new MemoryStream())
                    {
                        var buffer = new byte[4096];
                        int read;
                        try
                        {
                            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                                received.Write(buffer, 0, read);
                        }
                        catch (IOException)
                        {
                            // Connection: close — treat an abrupt close after data as end of response.
                        }

                        return Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
                    }
                }
            }
        }

        // Reads one HTTP request from the stream and returns its body (headers + Content-Length
        // framing), or null when the peer closed early.
        public static string ReadRequestBody(NetworkStream stream)
        {
            var received = new MemoryStream();
            var buffer = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                received.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            byte[] all = received.GetBuffer();
            int total = (int)received.Length;
            string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            int contentLength = ParseContentLength(headers);

            int bodyStart = headerEnd + 4;
            var bodyStream = new MemoryStream();
            if (total > bodyStart)
                bodyStream.Write(all, bodyStart, total - bodyStart);

            while (contentLength >= 0 && bodyStream.Length < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                bodyStream.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(bodyStream.GetBuffer(), 0, (int)bodyStream.Length);
        }

        public static void WriteJsonResponse(NetworkStream stream, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        static int FindHeaderEnd(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        static int ParseContentLength(string headers)
        {
            foreach (string line in headers.Split('\n'))
            {
                string trimmed = line.Trim();
                int colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                if (!trimmed.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                int value;
                if (int.TryParse(trimmed.Substring(colon + 1).Trim(), out value))
                    return value;
            }
            return -1;
        }
    }
}
