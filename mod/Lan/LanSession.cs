using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CWIOS.Lan
{
    /// <summary>
    /// Local (same Wi-Fi) multiplayer through the game's own host / join-by-code menus.
    ///
    /// HOST starts a small Photon server inside the app (LanServer) and points Photon at it instead of going
    /// offline; the game then creates its room as on PC, and the room name - the code on the sign in the house -
    /// carries the host's Wi-Fi address. JOIN ROOM decodes that address from the typed code and points Photon at
    /// the host. No Photon Cloud, Steam or any other server is involved (CWShim.Net still refuses anything that
    /// is not a local network address).
    ///
    /// Code: 6 characters like the PC game's. [0] any letter, [1] N..Z (the game reads it as "no mods"),
    /// [2..5] base-32 of (last two bytes of the host's IPv4 address &lt;&lt; 4 | check). The first two bytes are the
    /// joining phone's own, so host and guests must share a /16 network (every home Wi-Fi and iPhone hotspot does).
    /// </summary>
    static class LanSession
    {
        const string GameApp = "cw-ios-lan-game";    // stand-ins for the Photon app IDs: rooms are kept per app,
        const string VoiceApp = "cw-ios-lan-voice";  // so game and voice must differ (never Landfall's IDs)
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I
        const string NoModsLetters = "NPQRSTUVWXYZ";
        const string MenuScene = "NewMainMenuOptimized";

        static LanServer s_Server;
        static bool s_Hosting, s_WasInRoom, s_SawConnecting, s_PendingOffline;
        static float s_Started;
        static string s_HostAddress = "", s_JoinAddress = "", s_Code = "";

        public static bool Active => Offline.LanActive;
        public static bool Hosting => Active && s_Hosting;
        public static string Code => s_Code;
        public static string HostAddress => s_HostAddress;
        public static int Players => s_Server != null && s_Server.Running ? s_Server.GetInfo().Players : 0;

        public static void Init(GameObject host)
        {
            CWShim.Lan.OnHost = BeforeHost;
            CWShim.Lan.OnJoin = BeforeJoin;
            CWShim.Lan.OnRoomName = RoomName;
            host.AddComponent<LanBanner>();
        }

        // ---- host ------------------------------------------------------------------------------------
        static void BeforeHost()
        {
            End(null);
            s_HostAddress = LocalAddress();
            try
            {
                if (s_Server == null) s_Server = new LanServer();
                if (!s_Server.Running) s_Server.Start(LanServer.DefaultPort);
            }
            catch (Exception e)
            {
                Log.Error($"LAN server could not start ({e.Message}) - this game is offline (solo only)");
                return;
            }
            s_Server.HostUserId = CWShim.Identity.UserID.ToString();
            s_Server.GameAppId = GameApp;
            s_Hosting = true;
            Begin("127.0.0.1");
            TouchLocalNetwork(s_HostAddress);
            Log.Info($"LAN hosting on {s_HostAddress}:{LanServer.DefaultPort}");
        }

        static string RoomName(string generated)
        {
            if (!Hosting) return generated;
            s_Code = MakeCode(s_HostAddress) ?? generated;
            Log.Info($"LAN room code {s_Code}" + (s_Code == generated ? " (no Wi-Fi address: only this phone can play)" : ""));
            return s_Code;
        }

        // ---- join ------------------------------------------------------------------------------------
        static bool BeforeJoin(string room)
        {
            // already pointed at the host (the game calls JoinRoom again after the region check)
            if (Active && !s_Hosting) return false;
            End(null);
            string code = (room ?? "").Trim().ToUpperInvariant();
            string own = LocalAddress();
            string address = DecodeCode(code, own);
            if (address == null)
            {
                Log.Warn($"LAN join: '{code}' is not a Wi-Fi game code (own address {own})");
                ShowError("Can't join " + code,
                    own == "?" ? "This iPhone is not on Wi-Fi. Join the same Wi-Fi as the host, then try again."
                               : "That code doesn't look right. Type the 6 characters from the sign in the host's house.");
                return true;
            }
            s_Hosting = false;
            s_Code = code;
            Begin(address);
            TouchLocalNetwork(address);
            Log.Info($"LAN joining {code} at {address}:{LanServer.DefaultPort}");
            return false;
        }

        // ---- shared ----------------------------------------------------------------------------------
        static void Begin(string address)
        {
            s_JoinAddress = address;
            s_WasInRoom = s_SawConnecting = s_PendingOffline = false;
            s_Started = Time.unscaledTime;
            Offline.LanActive = true;
            var s = PhotonNetwork.PhotonServerSettings.AppSettings;
            s.UseNameServer = false;
            s.Server = address;
            s.Port = LanServer.DefaultPort;
            s.Protocol = ConnectionProtocol.Tcp;
            s.EnableProtocolFallback = false;
            s.FixedRegion = null;
            s.AppIdRealtime = GameApp;
            s.AppIdVoice = VoiceApp;
            PhotonNetwork.OfflineMode = false;   // the game's Host / JoinRoom only go online when this is off
        }

        /// Back to offline (solo) play. why == null: quietly, before a new host / join.
        public static void End(string why)
        {
            if (!Active && (s_Server == null || !s_Server.Running)) return;
            if (why != null) Log.Info("LAN session over: " + why);
            s_Server?.Stop();
            s_Hosting = false;
            s_Code = "";
            Offline.LanActive = false;
            Offline.ScrubServerSettings();
            if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect();
            s_PendingOffline = true;
        }

        /// Called every frame (LanBanner.Update).
        internal static void Tick()
        {
            if (s_PendingOffline && !Active)
            {
                if (PhotonNetwork.IsConnected || PhotonNetwork.InRoom) return;
                s_PendingOffline = false;
                try { PhotonNetwork.OfflineMode = true; } catch (Exception e) { Log.Warn("offline mode: " + e.Message); }
                Log.Info("Photon offline mode on (LAN session ended)");
                return;
            }
            if (!Active) return;
            if (PhotonNetwork.InRoom) { s_WasInRoom = true; return; }
            if (SceneManager.GetActiveScene().name != MenuScene) return;   // the house is loading: it joins the room there
            if (s_WasInRoom) { End("left the game"); return; }

            var st = PhotonNetwork.NetworkClientState;
            if (st != ClientState.Disconnected && st != ClientState.PeerCreated) s_SawConnecting = true;
            float t = Time.unscaledTime - s_Started;
            bool lost = s_SawConnecting && st == ClientState.Disconnected && t > 2f;
            if (!lost && t < 25f) return;

            string where = s_Hosting ? "this iPhone's game server" : $"the host ({s_JoinAddress})";
            Log.Warn($"LAN: could not reach {where} (state {st}, {t:0}s)");
            bool hosting = s_Hosting;
            var mm = MainMenuHandler.Instance;
            if (mm != null)
            {
                mm.StopAllCoroutines();   // the game's "Connecting..." wait loop never gives up on its own
                try { mm.UIHandler.TransistionToPage<MainMenuMainPage>(); } catch (Exception e) { Log.Warn("menu: " + e.Message); }
            }
            End("could not connect");
            ShowError(hosting ? "Can't host" : "Can't join " + s_Code,
                hosting ? "The game server on this iPhone did not start. Try again, or restart the app."
                        : $"Couldn't reach the host at {s_JoinAddress}.\n\nBoth phones must be on the same Wi-Fi, the host must already be in the house, " +
                          "and Content Warning (or LiveContainer) needs \"Local Network\" allowed in iPhone Settings > Privacy & Security > Local Network.");
        }

        static void ShowError(string title, string body)
        {
            try { Modal.ShowError(title, body); }
            catch (Exception e) { Log.Warn("modal: " + e.Message); }
        }

        // ---- room code -------------------------------------------------------------------------------
        static string MakeCode(string address)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            var b = ip.GetAddressBytes();
            int v = (b[2] << 8) | b[3];
            int payload = (v << 4) | Check(v);
            var r = new System.Random();
            var chars = new char[6];
            chars[0] = Alphabet[r.Next(24)];   // a letter
            chars[1] = NoModsLetters[r.Next(NoModsLetters.Length)];
            for (int i = 5; i >= 2; i--) { chars[i] = Alphabet[payload & 31]; payload >>= 5; }
            return new string(chars);
        }

        static string DecodeCode(string code, string ownAddress)
        {
            if (code.Length != 6) return null;
            if (!IPAddress.TryParse(ownAddress, out var own) || own.AddressFamily != AddressFamily.InterNetwork) return null;
            int payload = 0;
            for (int i = 2; i < 6; i++)
            {
                int d = Alphabet.IndexOf(code[i]);
                if (d < 0) return null;
                payload = (payload << 5) | d;
            }
            int v = payload >> 4;
            if ((payload & 15) != Check(v)) return null;
            var b = own.GetAddressBytes();
            return $"{b[0]}.{b[1]}.{v >> 8}.{v & 255}";
        }

        static int Check(int v) => (int)(((uint)v * 2654435761u) >> 28);

        // ---- network helpers -------------------------------------------------------------------------
        /// This phone's address on the Wi-Fi (en0), or "?".
        public static string LocalAddress()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != "en0" || ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch (Exception e) { Log.Warn("LAN interfaces: " + e.Message); }
            try
            {
                // iPhone hotspot host: bridge100 etc.; any private IPv4 address that is up
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) &&
                            CWShim.Net.IsLocal(ua.Address.ToString()) && !ua.Address.ToString().StartsWith("169.254."))
                            return ua.Address.ToString();
                }
            }
            catch { }
            return "?";
        }

        /// One small UDP packet to the local network (discard port), so iOS asks for "Local Network" access
        /// now rather than silently blocking the first game connection.
        static void TouchLocalNetwork(string address)
        {
            try
            {
                if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip) || ip.AddressFamily != AddressFamily.InterNetwork) return;
                var b = ip.GetAddressBytes();
                var target = new IPAddress(new byte[] { b[0], b[1], b[2], (byte)(b[3] == 1 ? 2 : 1) });
                using (var u = new UdpClient(AddressFamily.InterNetwork)) u.Send(new byte[] { 0 }, 1, new IPEndPoint(target, 9));
            }
            catch (Exception e) { Log.Info("LAN local network check: " + e.Message); }
        }
    }

    /// Small "Wi-Fi game" line for the host in the house before the day starts (the code is also on the sign),
    /// and the per-frame LAN session check.
    sealed class LanBanner : MonoBehaviour
    {
        GUIStyle m_Style;

        void Update() => LanSession.Tick();

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || !LanSession.Hosting || !PhotonNetwork.InRoom) return;
            bool started = false;
            try { started = SurfaceNetworkHandler.HasStarted; } catch { }
            if (started) return;
            float h = Screen.height;
            if (m_Style == null) m_Style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, richText = true };
            m_Style.fontSize = Mathf.RoundToInt(h * 0.032f);
            int players = PhotonNetwork.CurrentRoom?.PlayerCount ?? 1;
            string text = LanSession.HostAddress == "?"
                ? "<b>No Wi-Fi</b> - only this iPhone can play. Join a Wi-Fi network and host again to play together."
                : $"Wi-Fi game  <b>{LanSession.Code}</b>  ·  {players} player{(players == 1 ? "" : "s")}  ·  friends on the same Wi-Fi: JOIN ROOM, type the code";
            Rect safe = Screen.safeArea;
            var r = new Rect(safe.x + h * 0.03f, h - safe.yMax + h * 0.015f, safe.width * 0.75f, h * 0.055f);
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.5f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(r.x + h * 0.015f, r.y, r.width - h * 0.03f, r.height), text, m_Style);
        }
    }
}
