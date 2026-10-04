using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MusicBar
{
    public sealed class SettingsStore
    {
        public readonly string DataDirectory;
        private readonly string settingsPath;
        public string LastError = "";
        public SettingsStore(string baseDirectory)
        {
            DataDirectory = Path.Combine(baseDirectory, "data");
            settingsPath = Path.Combine(DataDirectory, "settings.json");
        }
        public AppSettings Load()
        {
            var settings = new AppSettings();
            try
            {
                if (File.Exists(settingsPath))
                {
                    using (var stream = File.OpenRead(settingsPath))
                    {
                        var loaded = new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream) as AppSettings;
                        if (loaded != null) settings = loaded;
                    }
                }
            }
            catch (Exception ex) { LastError = "设置文件读取失败，已恢复默认值：" + ex.Message; }
            settings.Normalize();
            // A preview is an explicit action for this launch, never a saved fake playback state.
            settings.PreviewEnabled = false;
            return settings;
        }
        public bool Save(AppSettings settings)
        {
            try
            {
                settings.Normalize();
                Directory.CreateDirectory(DataDirectory);
                string temporary = settingsPath + ".tmp";
                using (var stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(stream, settings);
                if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
                else File.Move(temporary, settingsPath);
                LastError = "";
                return true;
            }
            catch (Exception ex) { LastError = "无法保存设置，请把工具放在可写文件夹：" + ex.Message; return false; }
        }
        private string OverridePath(string trackKey)
        {
            byte[] bytes;
            using (var sha = SHA256.Create()) bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(trackKey));
            return Path.Combine(DataDirectory, "overrides", BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() + ".json");
        }
        public LyricDocument LoadOverride(string trackKey)
        {
            try
            {
                string path = OverridePath(trackKey);
                if (File.Exists(path)) return new JavaScriptSerializer().Deserialize<LyricDocument>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex) { LastError = "本地歌词读取失败：" + ex.Message; }
            return null;
        }
        public bool SaveOverride(string trackKey, LyricDocument document)
        {
            try
            {
                string path = OverridePath(trackKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(document), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                return true;
            }
            catch (Exception ex) { LastError = "本地歌词保存失败：" + ex.Message; return false; }
        }
        public void RemoveOverride(string trackKey)
        {
            string path = OverridePath(trackKey);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
