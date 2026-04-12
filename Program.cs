using System.Net;

namespace ProxyServer
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Введите ip адрес прокси:");
            string strIP = Console.ReadLine();
            IPAddress ipProxy; 
            if (string.IsNullOrEmpty(strIP)) strIP = "127.0.0.2";
            try
            {
                ipProxy = IPAddress.Parse(strIP);
            }
            catch 
            {
                ipProxy = IPAddress.Parse("127.0.0.2");
            }
            Console.WriteLine("Введите порт прокси:");
            string strPort = Console.ReadLine();
            int portProxy;
            try
            {
                portProxy = int.Parse(strPort);
                if (portProxy < 0 || portProxy > 65535) throw new Exception();
            }
            catch
            {
                portProxy = 8888;
            }
            Console.WriteLine($"Установлено ip: {ipProxy}, port: {portProxy}");
            ProxyCore proxy = new ProxyCore(ipProxy, portProxy);
            await proxy.StartAsync();
        }
    }
}
