using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.Extraction
{
    /// <summary>Reads data sheets with a model running locally in Ollama (https://ollama.com). Nothing leaves the PC.</summary>
    internal sealed class OllamaExtractor : IExtractor
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

        static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
        }

        static string BaseUrl(AppSettings s)
        {
            string u = (s.OllamaUrl ?? "").Trim().TrimEnd('/');
            return u.Length > 0 ? u : "http://localhost:11434";
        }

        public async Task<ExtractionResult> ExtractAsync(IList<string> files, AppSettings settings, CancellationToken ct)
        {
            string model = (settings.OllamaModel ?? "").Trim();
            if (model.Length == 0) throw new ExtractionException("No Ollama model is set. Open Settings and choose one.");

            var text = new StringBuilder();
            var images = new List<object>();
            int pageCount = 0;
            List<TextLine> pdfLines = null;
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path)) throw new ExtractionException("File not found: " + path);
                if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var content = await Task.Run(() => PdfText.Read(path, 40), ct);
                    if (content.CharCount < 100)
                        throw new ExtractionException("This PDF has almost no selectable text (it may be a scan). Save its pages as images and add those instead.");
                    text.Append(PdfText.ToPlain(content, 30000)).Append('\n');
                    pageCount += content.PageCount;
                    pdfLines = content.Lines;
                }
                else
                {
                    string mediaType;
                    images.Add(Convert.ToBase64String(await Task.Run(() => ImageUtil.PrepareImage(path, out mediaType), ct)));
                    pageCount++;
                }
            }

            var message = new Dictionary<string, object>
            {
                { "role", "user" },
                { "content", Prompt.Build(text.ToString()) }
            };
            if (images.Count > 0) message["images"] = images;

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "stream", false },
                { "format", "json" },
                { "options", new Dictionary<string, object> { { "temperature", 0 }, { "num_ctx", 16384 }, { "num_predict", 6144 } } },
                { "messages", new List<object> { message } }
            };

            string response = await SendAsync(BaseUrl(settings) + "/api/chat", Json().Serialize(body), model, ct);

            var root = Json().DeserializeObject(response) as Dictionary<string, object>;
            var msg = root != null && root.ContainsKey("message") ? root["message"] as Dictionary<string, object> : null;
            string answer = msg != null && msg.ContainsKey("content") ? ResultParser.Str(msg["content"]) : "";
            string reason = root != null && root.ContainsKey("done_reason") ? ResultParser.Str(root["done_reason"]) : "";
            var parsed = ResultParser.Parse(answer, files[0], reason == "length");
            parsed.PageCount = pageCount;
            parsed.HasTextLayer = pdfLines != null;
            if (pdfLines != null) parsed.Lines = pdfLines;
            return parsed;
        }

        static async Task<string> SendAsync(string url, string json, string model, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                throw new ExtractionException("Ollama took too long to answer. Try fewer pages or a smaller model.");
            }
            catch (HttpRequestException ex)
            {
                throw new ExtractionException("Could not reach Ollama at " + url.Replace("/api/chat", "")
                    + ". Start Ollama and check the address in Settings. " + ex.Message);
            }

            using (resp)
            {
                string text = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode) return text;
                string detail = ErrorText(text);
                if ((int)resp.StatusCode == 404 || detail.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ExtractionException("The model '" + model + "' is not installed. Run:  ollama pull " + model);
                if (detail.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new ExtractionException("This model cannot read images. Choose a vision model in Settings, or add a PDF with text. " + detail);
                throw new ExtractionException("Ollama returned an error (" + (int)resp.StatusCode + "). " + detail);
            }
        }

        static string ErrorText(string body)
        {
            try
            {
                var root = Json().DeserializeObject(body) as Dictionary<string, object>;
                if (root != null && root.ContainsKey("error")) return ResultParser.Str(root["error"]);
            }
            catch { }
            return "";
        }

        /// <summary>Checks that Ollama answers and lists the installed models.</summary>
        public static async Task<List<string>> ListModelsAsync(string url, CancellationToken ct)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) url = "http://localhost:11434";
            string text;
            try
            {
                using (var resp = await Http.GetAsync(url + "/api/tags", ct))
                {
                    text = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) throw new ExtractionException("Ollama answered with an error (" + (int)resp.StatusCode + ").");
                }
            }
            catch (HttpRequestException ex)
            {
                throw new ExtractionException("Could not reach Ollama at " + url + ". Is it running? " + ex.Message);
            }

            var names = new List<string>();
            var root = Json().DeserializeObject(text) as Dictionary<string, object>;
            var models = root != null && root.ContainsKey("models") ? root["models"] as System.Collections.IEnumerable : null;
            if (models != null)
            {
                foreach (var m in models)
                {
                    var d = m as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("name")) names.Add(ResultParser.Str(d["name"]));
                }
            }
            return names;
        }
    }
}
