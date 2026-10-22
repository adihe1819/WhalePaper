using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WhalePaper.Services
{
    internal static class JsonStore
    {
        public static T Load<T>(string path) where T : class, new()
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    var value = serializer.ReadObject(stream) as T;
                    return value ?? new T();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Failed to read " + path, ex);
                TryBackupCorruptFile(path);
                return new T();
            }
        }

        public static void Save<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                var backup = path + ".bak";
                try
                {
                    File.Copy(path, backup, true);
                }
                catch
                {
                }
            }

            // File.Replace is atomic and preserves the destination on failure.
            if (File.Exists(path))
            {
                File.Replace(temp, path, null, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static void TryBackupCorruptFile(string path)
        {
            try
            {
                var backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(path, backup, true);
                AppLog.Warn("Corrupt data file backed up to " + backup);
            }
            catch
            {
            }
        }
    }
}
