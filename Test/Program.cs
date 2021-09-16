using System;
using System.Text;
using WataNekko.IO.Pipes;

namespace Test
{
    class Program
    {
        static void Main(string[] args)
        {
            var sv = new NamedPipeServer("hi");
            var cl = new NamedPipeClient("hi");
            sv.ConnectAsync();
            cl.ConnectAsync().Wait();

            cl.DataReceived += (s, e) =>
            {
                var r = new byte[cl.BytesInBuffer];
                cl.Read(r, 0, r.Length);
                Console.WriteLine(Encoding.Default.GetString(r));
            };

            var send = Encoding.Default.GetBytes("hello wroarlsd!");
            sv.Write(send, 0, send.Length);

            System.Threading.Thread.Sleep(100);

            cl.Disconnect();
            sv.Disconnect();
        }
    }
}
