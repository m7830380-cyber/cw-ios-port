using System;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CWIOS
{
    /// <summary>
    /// Solo play without any server: the game's own offline path (PhotonNetwork.OfflineMode, as after the PC
    /// "Play offline?" prompt). HOST creates an offline room and loads the house. The game's AppIds are removed
    /// from the Photon settings, and the patched PhotonPeer.Connect refuses anything but the local network anyway.
    /// </summary>
    static class Offline
    {
        /// Set by the LAN layer (later) while a local network game is being hosted or joined.
        public static bool LanActive;

        public static void ScrubServerSettings()
        {
            try
            {
                var s = PhotonNetwork.PhotonServerSettings?.AppSettings;
                if (s == null) { Log.Warn("Photon server settings not found"); return; }
                s.AppIdRealtime = s.AppIdChat = s.AppIdVoice = s.AppIdFusion = string.Empty;
                s.UseNameServer = false;
                s.Server = "127.0.0.1";
                s.Port = 0;
                s.FixedRegion = null;
                Log.Info("Photon cloud settings removed (offline / local network only)");
            }
            catch (Exception e)
            {
                Log.Error("Photon settings: " + e);
            }
        }

        public static void OnSceneLoaded(Scene s)
        {
            if (LanActive) return;
            try
            {
                // PhotonNetwork.Disconnect() (leaving a game) turns offline mode off again
                if (!PhotonNetwork.OfflineMode && !PhotonNetwork.InRoom)
                {
                    PhotonNetwork.OfflineMode = true;
                    Log.Info($"Photon offline mode on (state {PhotonNetwork.NetworkClientState})");
                }
            }
            catch (Exception e)
            {
                Log.Error("offline mode: " + e);
            }
        }
    }
}
