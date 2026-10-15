using System;
using System.Collections;
using System.IO;
using CWShim;
using Portningsbolaget.Platforms;
using UnityEngine;

namespace CWPlatform
{
    /// <summary>
    /// The game's platform layer on iOS, in place of SteamRuntimeManager (the patcher changes the
    /// "new SteamRuntimeManager()" in PlatformManager to "new IOSPlatform()"). No Steam, no servers:
    /// name and ID are local (CWShim.Identity), achievements are only logged, small platform files
    /// (the blocklist) live in Documents/Platform, and text dialogs use the iOS keyboard.
    /// </summary>
    public class IOSPlatform : IPlatform
    {
        public bool Initialized => true;
        public string NickName => Identity.NickName;
        public ulong UserID => Identity.UserID;
        public string FileOutputDirectory => Paths.Videos;
        public PlatformUtility.PlatformFamily PlatformFamily => PlatformUtility.PlatformFamily.IOS;
        public Action<string, string> OnJoinedSession { get; set; }

        public void InitializeAfterAssembliesLoaded() => Debug.Log("[CW-iOS] iOS platform (no Steam): " + NickName + " / " + UserID);
        public void InitializeAfterSceneLoad() { }
        public void Teardown() { }

        public void UnlockAchievement(Achievements achievement) => Debug.Log("[CW-iOS] achievement: " + achievement);
        public void ProgressAchievement(Achievements achievement) { }
        public void ProgressAchievement(Achievements achievement, int amount) { }
        public void SetAchievementProgress(Achievements achievement, int progress) { }
        public void ClearAchievementsProgress() { }

        // ---- text dialog (join code, chat, face text) on the iOS keyboard ---------------------------
        TouchScreenKeyboard m_Keyboard;
        Action<string, DialogueResult> m_OnDone;

        public void OpenDialog(string title, DialogType dialogType, Action<string, DialogueResult> onDone)
        {
            try
            {
                if (m_Keyboard != null) { m_Keyboard.active = false; Finish(string.Empty, DialogueResult.Aborted); }
                m_OnDone = onDone;
                m_Keyboard = TouchScreenKeyboard.Open(string.Empty, TouchScreenKeyboardType.Default, false,
                    dialogType == DialogType.Chat, false, false, title ?? string.Empty,
                    dialogType == DialogType.Chat ? 200 : dialogType == DialogType.JoinRoom ? 6 : 20);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[CW-iOS] keyboard: " + e.Message);
                m_OnDone = null;
                onDone?.Invoke(string.Empty, DialogueResult.Failed);
            }
        }

        void Finish(string text, DialogueResult r)
        {
            var cb = m_OnDone;
            m_OnDone = null;
            m_Keyboard = null;
            try { cb?.Invoke(text, r); } catch (Exception e) { Debug.LogException(e); }
        }

        public void Update()
        {
            if (m_Keyboard == null) return;
            switch (m_Keyboard.status)
            {
                case TouchScreenKeyboard.Status.Done: Finish(m_Keyboard.text ?? string.Empty, DialogueResult.Succeeded); break;
                case TouchScreenKeyboard.Status.Canceled:
                case TouchScreenKeyboard.Status.LostFocus: Finish(string.Empty, DialogueResult.Aborted); break;
            }
        }

        // ---- online checks: the iOS app never talks to platform or Landfall servers ------------------
        public void VerifyString(string s, Action<string> callback) => callback?.Invoke(s);
        public void HasInternetConnection(Action<bool> callback) => callback?.Invoke(true);
        public void CanCommunicate(Action<bool> callback) => callback?.Invoke(true);
        public void CanCommunicateWithUser(ulong targetUserID, Action<bool> callback) => callback?.Invoke(true);
        public void GetAvatarAsync(Action<Texture2D> callback) => callback?.Invoke(Texture2D.grayTexture);
        public void GetAvatarAsync(ulong xuid, Action<Texture2D> callback) => callback?.Invoke(Texture2D.grayTexture);

        // ---- platform files (blocklist) in Documents/Platform ----------------------------------------
        static string FilePath(string fileName, int slot)
        {
            string dir = Path.Combine(Paths.Documents, "Platform");
            Directory.CreateDirectory(dir);
            string safe = string.Concat((fileName ?? "file").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(dir, slot >= 0 ? safe + "_" + slot : safe);
        }

        public void RequestSaveExists(string fileName, Action<FileResult> done) => RequestSaveExists(fileName, -1, done);
        public void RequestSaveExists(string fileName, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { r = File.Exists(FilePath(fileName, saveSlot)) ? FileResult.Succeeded : FileResult.NotFound; }
            catch (Exception) { r = FileResult.Failed; }
            done?.Invoke(r);
        }

        public void RequestSave(string fileName, byte[] data, Action<FileResult> done) => RequestSave(fileName, data, -1, done);
        public void RequestSave(string fileName, byte[] data, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { File.WriteAllBytes(FilePath(fileName, saveSlot), data ?? new byte[0]); r = FileResult.Succeeded; }
            catch (Exception e) { Debug.LogWarning("[CW-iOS] save " + fileName + ": " + e.Message); r = FileResult.Failed; }
            done?.Invoke(r);
        }

        public void RequestLoadAsync(string fileName, Action<FileResult, byte[]> done) => RequestLoadAsync(fileName, -1, done);
        public void RequestLoadAsync(string fileName, int saveSlot, Action<FileResult, byte[]> done)
        {
            Load(fileName, saveSlot, out var r, out var data);
            done?.Invoke(r, data);
        }

        public IEnumerator RequestLoad(string fileName, Action<FileResult, byte[]> done) => RequestLoad(fileName, -1, done);
        public IEnumerator RequestLoad(string fileName, int saveSlot, Action<FileResult, byte[]> done)
        {
            Load(fileName, saveSlot, out var r, out var data);
            done?.Invoke(r, data);
            yield break;
        }

        static void Load(string fileName, int slot, out FileResult r, out byte[] data)
        {
            data = null;
            try
            {
                string p = FilePath(fileName, slot);
                if (!File.Exists(p)) { r = FileResult.NotFound; return; }
                data = File.ReadAllBytes(p);
                r = FileResult.Succeeded;
            }
            catch (Exception e) { Debug.LogWarning("[CW-iOS] load " + fileName + ": " + e.Message); r = FileResult.Failed; }
        }

        public void RequestDelete(string fileName, Action<FileResult> done) => RequestDelete(fileName, -1, done);
        public void RequestDelete(string fileName, int saveSlot, Action<FileResult> done)
        {
            FileResult r;
            try { File.Delete(FilePath(fileName, saveSlot)); r = FileResult.Succeeded; }
            catch (Exception) { r = FileResult.Failed; }
            done?.Invoke(r);
        }
    }
}
