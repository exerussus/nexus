using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>JSON-утилиты демона: разбор без исключений, чтение полей, атомарная запись файлов.</summary>
    internal static class Json
    {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = false,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };

        public static JsonObject TryParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonNode.Parse(text.TrimStart('﻿')) as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        public static JsonObject ReadFile(string path, long maxBytes = 1 << 20)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > maxBytes) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Utf8, true);
                return TryParseObject(reader.ReadToEnd());
            }
            catch
            {
                return null;
            }
        }

        public static string Str(JsonNode o, string key)
        {
            try
            {
                var v = o?[key];
                if (v == null) return null;
                return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
            }
            catch
            {
                return null;
            }
        }

        public static double Num(JsonNode o, string key, double fallback = 0)
        {
            try
            {
                var v = o?[key];
                if (v == null) return fallback;
                switch (v.GetValueKind())
                {
                    case JsonValueKind.Number: return v.GetValue<double>();
                    case JsonValueKind.String:
                        return double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
                    default: return fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        public static bool Bool(JsonNode o, string key, bool fallback = false)
        {
            try
            {
                var v = o?[key];
                if (v == null) return fallback;
                return v.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => fallback,
                };
            }
            catch
            {
                return fallback;
            }
        }

        public static string[] StrArray(JsonNode o, string key)
        {
            if (!(o?[key] is JsonArray arr)) return Array.Empty<string>();
            var result = new string[arr.Count];
            for (var i = 0; i < arr.Count; i++)
                result[i] = arr[i] == null ? string.Empty : arr[i].GetValueKind() == JsonValueKind.String ? arr[i].GetValue<string>() : arr[i].ToJsonString();
            return result;
        }

        public static string Write(JsonNode node) => node == null ? "null" : node.ToJsonString(Options);

        /// <summary>Запись через .tmp с заменой; файл может держать открытым клиент — несколько повторов.</summary>
        public static bool WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temp, content, Utf8);
                    File.Move(temp, path, true);
                    return true;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(20);
                }
                catch (Exception e)
                {
                    Log.Warn($"запись {path}: {e.Message}");
                    try
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    catch
                    {
                        // уберём при следующей уборке
                    }

                    return false;
                }
            }
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Log.Warn($"удаление {path}: {e.Message}");
            }
        }
    }
}
