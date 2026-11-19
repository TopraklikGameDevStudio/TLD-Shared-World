using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TLDCoop
{
    /// <summary>
    /// Playing from different homes: the host's modem has to let the friend's packets in. Most home modems accept a
    /// request for that (UPnP): this asks the modem to send UDP on the game port to this PC while the server is open,
    /// takes the rule away again when it closes, and finds the internet address to give to the friend. Everything runs
    /// on a background thread; the result is only read by the main thread.
    /// </summary>
    internal static class PortOpener
    {
        internal static volatile string Status = "not tried";
        internal static volatile string PublicAddress;
        internal static volatile bool Opened;
        private static string _control, _service, _lan; private static int _port;
        internal static readonly System.Collections.Generic.List<string> Locations = new();

        internal static void OpenAsync(int port, string lan)
        {
            _port = port; _lan = lan; Opened = false; PublicAddress = null; Status = "asking the modem…";
            var t = new Thread(() =>
            {
                try { Open(); }
                catch (Exception e) { Status = "failed: " + e.Message; }
                try { if (PublicAddress == null) PublicAddress = AskWeb(); } catch { }
            }) { IsBackground = true, Name = "TLDCoop port opener" };
            t.Start();
        }

        private static void Open()
        {
            if (string.IsNullOrEmpty(_lan)) { Status = "no home-network address found"; return; }
            Locations.Clear();
            if (Discover() == null) { Status = "no modem answered (UPnP is off on the modem, or not supported)"; return; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string loc = null, xml = null;
            foreach (var l in Locations)
            {
                try { var x = http.GetStringAsync(l).Result; if (x.Contains("WANIPConnection") || x.Contains("WANPPPConnection")) { loc = l; xml = x; break; } } catch { }
            }
            if (xml == null) { Status = $"{Locations.Count} device(s) answered, none of them is a modem that forwards ports"; return; }
            foreach (var svc in new[] { "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
            {
                int i = xml.IndexOf(svc, StringComparison.Ordinal);
                if (i < 0) continue;
                var m = Regex.Match(xml.Substring(i), @"<controlURL>\s*([^<]+?)\s*</controlURL>");
                if (!m.Success) continue;
                _control = new Uri(new Uri(loc), m.Groups[1].Value).ToString(); _service = svc;
                break;
            }
            if (_control == null) { Status = "the modem has no port-forwarding service"; return; }
            string args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{_port}</NewExternalPort><NewProtocol>UDP</NewProtocol>"
                        + $"<NewInternalPort>{_port}</NewInternalPort><NewInternalClient>{_lan}</NewInternalClient><NewEnabled>1</NewEnabled>"
                        + $"<NewPortMappingDescription>TLD Ortak Dunya</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>";
            string res = Soap(http, "AddPortMapping", args);
            if (res == null) { Status = "the modem refused to open the port"; return; }
            Opened = true;
            string ip = Soap(http, "GetExternalIPAddress", "");
            var mi = ip != null ? Regex.Match(ip, @"<NewExternalIPAddress>\s*([0-9.]+)\s*</NewExternalIPAddress>") : null;
            if (mi != null && mi.Success && !IsPrivate(mi.Groups[1].Value)) PublicAddress = mi.Groups[1].Value;
            else if (mi != null && mi.Success) Status = "port opened, but the modem itself sits behind another network (" + mi.Groups[1].Value + "): friends from outside may still not reach you";
            if (Status.StartsWith("asking")) Status = $"UDP {_port} opened on the modem";
        }

        internal static void Close()
        {
            if (!Opened || _control == null) return;
            Opened = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                Soap(http, "DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{_port}</NewExternalPort><NewProtocol>UDP</NewProtocol>");
                Status = $"UDP {_port} closed again on the modem";
            }
            catch { }
        }

        private static string Discover()
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(_lan), 0));
            udp.Client.ReceiveTimeout = 2500;
            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                                       "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1", "upnp:rootdevice" })
            {
                var data = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
                udp.Send(data, data.Length, ep);
            }
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    IPEndPoint from = null;
                    var got = Encoding.ASCII.GetString(udp.Receive(ref from));
                    var m = Regex.Match(got, @"(?im)^location:\s*(\S+)");
                    if (m.Success && !Locations.Contains(m.Groups[1].Value)) Locations.Add(m.Groups[1].Value);
                }
                catch (SocketException) { break; }
            }
            return Locations.Count > 0 ? Locations[0] : null;
        }

        private static string Soap(HttpClient http, string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
                        + $"<u:{action} xmlns:u=\"{_service}\">{args}</u:{action}></s:Body></s:Envelope>";
            var msg = new HttpRequestMessage(HttpMethod.Post, _control) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
            msg.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_service}#{action}\"");
            var r = http.SendAsync(msg).Result;
            string text = r.Content.ReadAsStringAsync().Result;
            return r.IsSuccessStatusCode ? text : null;
        }

        private static string AskWeb()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ip = http.GetStringAsync("https://api.ipify.org").Result.Trim();
            return Regex.IsMatch(ip, @"^\d+\.\d+\.\d+\.\d+$") ? ip : null;
        }

        private static bool IsPrivate(string ip)
        {
            var b = IPAddress.Parse(ip).GetAddressBytes();
            return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
    }
}
