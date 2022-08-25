using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using JapeCore;
using JapeService;
using JapeWeb;

namespace Cujoe
{
    internal class Program : ConsoleProgram<int, int, string>
    {
        protected override string DefaultLog => "server.log";

        private int http;
        private int https;
        private string contentPath;

        private static async Task Main(string[] args) => await RunAsync<Program>(args);

        protected override ICommandArg[] Args() => WebServer.Args;

        protected override void OnSetup(int http, int https, string contentPath)
        {
            this.http = http;
            this.https = https;
            this.contentPath = contentPath;
        }

        protected override async Task OnStartAsync()
        {
            SyncReload();
            WebServer webServer = new(http, https, contentPath);
            await webServer.Start();
        }

        private static void SyncReload()
        {
            #if DEBUG
            try
            {
                File.WriteAllText(".sync", DateTime.Now.ToString(CultureInfo.InvariantCulture));
            }
            catch
            { 
                Log.Write("Error: Sync File");
            }
            #endif
        }
    }
}
