using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MCLauncher {

    class BadUpdateIdentityException: ArgumentException{
        public BadUpdateIdentityException() : base("Bad updateIdentity") { }
    }

    class VersionDownloader {

        private static readonly HttpClient client = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
        private WUProtocol protocol = new WUProtocol();
        
        private async Task<XDocument> PostXmlAsync(string url, XDocument data, CancellationToken cancellationToken) {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            using (var stringWriter = new StringWriter()) {
                using (XmlWriter xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = true })) {
                    data.Save(xmlWriter);
                }
                request.Content = new StringContent(stringWriter.ToString(), Encoding.UTF8, "application/soap+xml");
            }
            using (var resp = await client.SendAsync(request, cancellationToken)) {
                string str = await resp.Content.ReadAsStringAsync();
                return XDocument.Parse(str);
            }
        }

        private async Task DownloadFile(string url, string to, DownloadProgress progress, CancellationToken cancellationToken) {
            FileStream outStream = null;
            try {
                using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)) {
                    resp.EnsureSuccessStatusCode();
                    using (var inStream = await resp.Content.ReadAsStreamAsync()) {
                        outStream = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                        long? totalSize = resp.Content.Headers.ContentLength;
                        progress(0, totalSize);
                        long transferred = 0;
                        byte[] buf = new byte[1024 * 1024];
                        while (true) {
                            int n = await inStream.ReadAsync(buf, 0, buf.Length, cancellationToken);
                            if (n == 0)
                                break;
                            await outStream.WriteAsync(buf, 0, n, cancellationToken);
                            transferred += n;
                            progress(transferred, totalSize);
                        }
                        await outStream.FlushAsync(cancellationToken);
                        outStream.Dispose();
                        outStream = null;
                    }
                }
            } catch (Exception ex) {
                Debug.WriteLine($"DownloadFile error for {url} -> {to}: {ex}");
                // Cleanup incomplete file on error
                outStream?.Dispose();
                if (File.Exists(to)) {
                    try { File.Delete(to); } catch { /* Best effort cleanup */ }
                }
                throw;
            }
        }

        private async Task<string> GetDownloadUrl(string updateIdentity, string revisionNumber, CancellationToken cancellationToken) {
            XDocument result = await PostXmlAsync(protocol.GetDownloadUrl(),
                protocol.BuildDownloadRequest(updateIdentity, revisionNumber), cancellationToken);
            Debug.WriteLine($"GetDownloadUrl() response for updateIdentity {updateIdentity}, revision {revisionNumber}:\n{result.ToString()}");
            foreach (string s in protocol.ExtractDownloadResponseUrls(result)) {
                if (s.StartsWith("http://") || s.StartsWith("https://"))
                    return s;
            }
            return null;
        }

        public Task<string> ResolveDownloadUrl(string updateIdentity, string revisionNumber, CancellationToken cancellationToken)
        {
            return GetDownloadUrl(updateIdentity, revisionNumber, cancellationToken);
        }

        public void EnableUserAuthorization() {
            protocol.SetMSAUserToken(WUTokenHelper.GetWUToken());
        }

        public async Task Download(string updateIdentity, string revisionNumber, string destination, DownloadProgress progress, CancellationToken cancellationToken) {
            string link = await GetDownloadUrl(updateIdentity, revisionNumber, cancellationToken);
            if (link == null)
                throw new BadUpdateIdentityException();
            Debug.WriteLine("Resolved download link: " + link);
            await DownloadFile(link, destination, progress, cancellationToken);
        }

        public delegate void DownloadProgress(long current, long? total);



    }
}
