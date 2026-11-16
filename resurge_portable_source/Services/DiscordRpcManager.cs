using DiscordRPC;
using System;

namespace ResurgeLauncherCSharp.Services
{
    public class DiscordRpcManager : IDisposable
    {
        private DiscordRpcClient client;
        // The real Client ID provided by the user
        private const string ApplicationId = "1519089118074896525"; 

        public DiscordRpcManager()
        {
            client = new DiscordRpcClient(ApplicationId);
            client.Initialize();
        }

        public void SetStatusInLauncher()
        {
            client.SetPresence(new RichPresence()
            {
                Details = "Playing Resurge Black Ops 3",
                State = "In Launcher",
                Assets = new Assets()
                {
                    LargeImageKey = "logo",
                    LargeImageText = "Resurge Launcher",
                }
            });
        }

        public void SetStatusInGame()
        {
            client.SetPresence(new RichPresence()
            {
                Details = "Playing Resurge Black Ops 3",
                State = "In Game",
                Assets = new Assets()
                {
                    LargeImageKey = "logo",
                    LargeImageText = "Resurge Launcher",
                }
            });
        }

        public void Dispose()
        {
            client?.Dispose();
        }
    }
}
