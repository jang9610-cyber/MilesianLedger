using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MabinogiBarter
{
    // Public deployment address only. This file never contains a provider key.
    public sealed class AuctionProxyConfig
    {
        public const string NotConfiguredMessage = "경매장 서비스 연결을 준비 중입니다. 기존 시세와 재료 준비 기능은 계속 사용할 수 있습니다.";
        public const string InvalidConfigurationMessage = "경매장 서비스 주소를 확인하지 못했습니다. 기존 시세와 재료 준비 기능은 계속 사용할 수 있습니다.";
        public Uri BaseUri { get; private set; }
        public bool IsConfigured { get { return BaseUri != null; } }
        public string StatusMessage { get; private set; }

        public AuctionProxyConfig(string baseUrl)
        {
            StatusMessage = NotConfiguredMessage;
            if (String.IsNullOrWhiteSpace(baseUrl)) return;
            StatusMessage = InvalidConfigurationMessage;
            if (baseUrl.Length > 2048 || baseUrl != baseUrl.Trim() || baseUrl.IndexOf('\\') >= 0) return;
            foreach (char c in baseUrl) if (Char.IsControl(c) || Char.IsWhiteSpace(c)) return;
            Uri parsed;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out parsed) || !String.IsNullOrEmpty(parsed.UserInfo)
                || !String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment)
                || baseUrl.IndexOf('?') >= 0 || baseUrl.IndexOf('#') >= 0) return;
            string host = parsed.DnsSafeHost.TrimEnd('.');
            if (String.IsNullOrEmpty(host) || host.Equals("nexon.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".nexon.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("nexon.co.kr", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".nexon.co.kr", StringComparison.OrdinalIgnoreCase)) return;
            if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)) return;
            // Keep an optional reverse-proxy path prefix when appending API routes.
            BaseUri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
            StatusMessage = "";
        }

        public static AuctionProxyConfig Load(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return new AuctionProxyConfig("");
                if (new FileInfo(filePath).Length > 16384) return Invalid();
                var root = new JavaScriptSerializer { MaxJsonLength = 16384 }.DeserializeObject(File.ReadAllText(filePath, Encoding.UTF8)) as IDictionary<string, object>;
                object value;
                if (root == null || !root.TryGetValue("BaseUrl", out value) || !(value is string)) return Invalid();
                return new AuctionProxyConfig((string)value);
            }
            catch { return Invalid(); }
        }

        private static AuctionProxyConfig Invalid()
        {
            return new AuctionProxyConfig("") { StatusMessage = InvalidConfigurationMessage };
        }
    }
}
