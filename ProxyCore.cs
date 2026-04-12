using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace ProxyServer
{
    public class ProxyCore
    {
        public IPAddress IP { get; set; }
        public int Port { get; set; }
        private Socket socket;

        public ProxyCore(IPAddress ip, int port)
        {
            IP = ip;
            Port = port;
        }

        public async Task StartAsync()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.Listen(20);
            while (true)
            {
                Socket BrowserToProxy = await socket.AcceptAsync();
                _ = HandleConnectionWithBrowserAsync(BrowserToProxy);
            }
        }

        public async Task HandleConnectionWithBrowserAsync(Socket BrowserToProxy)
        {
            byte[] Buffer = new byte[16384];
            StringBuilder HttpBuilder = new StringBuilder();
            while (BrowserToProxy != null && BrowserToProxy.Connected)
            {
                int BytesRead = await BrowserToProxy.ReceiveAsync(Buffer);
                if (BytesRead <= 0) return;
                string Chunk = Encoding.ASCII.GetString(Buffer, 0, BytesRead);
                HttpBuilder.Append(Chunk);
                if (HttpBuilder.ToString().Contains("\r\n\r\n"))
                {
                    string Http = HttpBuilder.ToString();
                    HttpBuilder.Clear();
                    await HandleConnectionHTTPAsync(BrowserToProxy, Http);
                }
            }
        }

        public async Task HandleConnectionHTTPAsync(Socket BrowserToProxy, string Http)
        {
            string[] parts = Http.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (parts.Length == 0) return;

            string[] partparts = parts[0].Split(' ');
            string method = partparts[0];
            string fullURL = partparts[1];
            string versionHTTP = partparts[2];

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int headerEnd = 1;

            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i]))
                {
                    headerEnd = i;
                    break;
                }

                int indexColon = parts[i].IndexOf(':');
                if (indexColon > 0)
                {
                    string key = parts[i].Substring(0, indexColon).Trim();
                    string value = parts[i].Substring(indexColon + 1).Trim();
                    headers[key] = value;
                }
            }

            string host = null;
            if (headers.ContainsKey("Host")) host = headers["Host"];

            if (host == null)
            {
                await BrowserToProxy.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"));
                return;
            }

            string targetHost = host;
            int targetPort = 80;

            if (host.Contains(":"))
            {
                string[] partss = host.Split(':');
                targetHost = partss[0];
                int.TryParse(partss[1], out targetPort);
            }

            try
            {
                string addrForBlackList = "/";

                try
                {
                    addrForBlackList = new Uri(fullURL).Host;

                    if (CheckBlackList(addrForBlackList))
                    {
                        string message = "Blocked";
                        string response = $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n{message}";
                        await BrowserToProxy.SendAsync(Encoding.ASCII.GetBytes(response));
                        BrowserToProxy.Shutdown(SocketShutdown.Both);
                        BrowserToProxy.Close();
                        Console.WriteLine($"{addrForBlackList} заблокирован");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }

                using (Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    var addresses = await Dns.GetHostAddressesAsync(targetHost);
                    var ipv4Addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
                    if (ipv4Addresses.Length == 0)
                    {
                        await BrowserToProxy.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n"));
                        return;
                    }
                    var targetIP = ipv4Addresses[0];
                    IPEndPoint remIP = new IPEndPoint(targetIP, targetPort);
                    await server.ConnectAsync(remIP);
                    string path = "/";

                    try { path = new Uri(fullURL).PathAndQuery; }
                    catch { }

                    string newRequest = $"{method} {path} {versionHTTP}\r\n";

                    for (int i = 1; i < headerEnd; i++) newRequest += parts[i] + "\r\n";

                    newRequest += "\r\n";
                    byte[] requestBytes = Encoding.ASCII.GetBytes(newRequest);
                    await server.SendAsync(requestBytes);

                    byte[] buffer = new byte[16384];
                    int bytesRead;
                    bool firstChunk = true;
                    string statusLine = "";

                    while ((bytesRead = await server.ReceiveAsync(buffer)) > 0)
                    {
                        await BrowserToProxy.SendAsync(new ArraySegment<byte>(buffer, 0, bytesRead));

                        if (firstChunk)
                        {
                            string responseText = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                            string[] responseLines = responseText.Split(new[] { "\r\n" }, StringSplitOptions.None);

                            if (responseLines.Length > 0)
                            {
                                statusLine = responseLines[0];
                                Console.WriteLine($"{fullURL} {statusLine}");
                            }

                            firstChunk = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);

                if (BrowserToProxy.Connected)
                {
                    await BrowserToProxy.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n"));

                    BrowserToProxy.Shutdown(SocketShutdown.Both);
                    BrowserToProxy.Close();
                }
            }
        }


        public bool CheckBlackList(string addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return false;
            string domainToCheck = ConvertAddress(addr);
            try
            {
                string[] parts = File.ReadAllLines("blacklist.txt");
                foreach (var part in parts)
                {
                    string blackDomain = part.Trim().ToLower();
                    if (string.IsNullOrEmpty(blackDomain)) continue;
                    if (domainToCheck.EndsWith(blackDomain)) return true;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Ошибка черного списка: " + e.Message);
            }

            return false;
        }


        private string ConvertAddress(string host)
        {
            host = host.ToLower().Trim();
            if (host.StartsWith("http://")) host = host.Substring(7);
            else if (host.StartsWith("https://")) host = host.Substring(8);
            if (host.StartsWith("www.")) host = host.Substring(4);
            return host;
        }

    }
}
