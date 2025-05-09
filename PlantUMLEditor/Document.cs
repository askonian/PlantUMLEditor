﻿using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;

using Microsoft.VisualStudio.Text;

using PlantUml.Net;

using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PlantUMLEditor;

public class Document : IDisposable
{
    private IPlantUmlRenderer plantUmlRenderer;

    [ThreadStatic]
    private static StringWriter htmlWriterStatic;

    [ThreadStatic]
    private static HttpClient httpClient;

    protected virtual IPlantUmlRenderer PlantUmlRenderer
    {
        get
        {
            if (plantUmlRenderer == null)
            {
                plantUmlRenderer = new RendererFactory()
                    .CreateRenderer(new PlantUmlSettings
                    {
                        JavaPath = AdvancedOptions.Instance.Path
                    });
            }

            return plantUmlRenderer;
        }
    }

    public Func<string> GetText { get; set; }

    public bool IsParsing { get; private set; }

    public string ParsedResult { get; private set; }

    public event Action<Document> Parsed;

    private string currentText;

    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
       .UseAdvancedExtensions()
       .UsePragmaLines()
       .UsePreciseSourceLocation()
       .UseYamlFrontMatter()
       .UseEmojiAndSmiley()
       .Build();

    public MarkdownDocument Markdown { get; private set; }

    public async Task ParseAsync()
    {
        IsParsing = true;
        bool success = false;

        try
        {
            var text = GetText?.Invoke()?.Trim();

            if (currentText != text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var nodes = new Dictionary<string, string>();

                    text = ReplaceUmlWithPlaceholder("@startuml", "@enduml", text, ref nodes);
                    text = ReplaceUmlWithPlaceholder("@startmindmap", "@endmindmap", text, ref nodes);
                    text = ReplaceUmlWithPlaceholder("@startgantt", "@endgantt", text, ref nodes);
                    text = ReplaceUmlWithPlaceholder("@startwbs", "@endwbs", text, ref nodes);
                    text = ReplaceUmlWithPlaceholder("@startjson", "@endjson", text, ref nodes);

                    var md = await RenderMarkdownAsync(text);

                    foreach (var pumlNode in nodes)
                    {
                        var data = await RenderPlantUMLAsync(pumlNode.Value);

                        md = md.Replace($"<!--- PlantUML:{pumlNode.Key} -->", data);
                    }

                    ParsedResult = md;
                }
                else
                {
                    ParsedResult = "Empty";
                }

                currentText = text;
                success = true;
            }
        }
        catch (Exception ex)
        {
            await ex.LogAsync();

            ParsedResult = "<p>An unexpected exception occurred:</p><pre>" +
                    ex.ToString().Replace("<", "&lt;").Replace("&", "&amp;") + "</pre>";
        }
        finally
        {
            IsParsing = false;

            if (success)
            {
                Parsed?.Invoke(this);
            }
        }
    }

    protected virtual async Task<string> RenderMarkdownAsync(string data)
    {
        // Generate the HTML document
        StringWriter htmlWriter = null;
        try
        {
            var md = Markdig.Markdown.Parse(data, Pipeline);

            htmlWriter = (htmlWriterStatic ??= new StringWriter());
            htmlWriter.GetStringBuilder().Clear();

            var htmlRenderer = new HtmlRenderer(htmlWriter);
            Document.Pipeline.Setup(htmlRenderer);
            htmlRenderer.UseNonAsciiNoEscape = true;
            htmlRenderer.Render(md);

            await htmlWriter.FlushAsync();
            string html = htmlWriter.ToString();
            html = Regex.Replace(html, "\"language-(c|C)#\"", "\"language-csharp\"", RegexOptions.Compiled);
            return html;
        }
        catch (Exception ex)
        {
            // We could output this to the exception pane of VS?
            // Though, it's easier to output it directly to the browser
            return "<p>An unexpected exception occurred:</p><pre>" +
                    ex.ToString().Replace("<", "&lt;").Replace("&", "&amp;") + "</pre>";
        }
        finally
        {
            // Free any resources allocated by HtmlWriter
            htmlWriter?.GetStringBuilder().Clear();
        }
    }

    protected virtual async Task<string> RenderPlantUMLAsync(string data)
    {
        string result;

        if (AdvancedOptions.Instance.RenderType == RenderType.Local)
        {
            var bytes = await PlantUmlRenderer.RenderAsync(data, OutputFormat.Svg);

            result = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            httpClient = (httpClient ??= new HttpClient()
            {
                BaseAddress = new Uri(AdvancedOptions.Instance.RemoteUrl)
            });

            var request = new HttpRequestMessage(HttpMethod.Post, "RenderFromPlain")
            {
                Content = new StringContent(data, Encoding.UTF8, "text/plain")
            };

            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                result = await response.Content.ReadAsStringAsync();
            }
            else
            {
                result = "Error";
            }
        }

        return result;
    }

    protected virtual string ReplaceUmlWithPlaceholder(string startTag, string endTag, string data, ref Dictionary<string, string> nodes)
    {
        var sb = new StringBuilder();

        var start = 0;
        var umlNodeFound = false;

        foreach (Match group in new Regex($"({startTag}.*?{endTag})", RegexOptions.Singleline).Matches(data))
        {
            var umlNode = ParseUmlNode(startTag, group.Value);

            sb.Append(data.Substring(start, group.Index - start));
            sb.Append($"<!--- PlantUML:{umlNode.Key} -->");

            start = group.Index + group.Length;

            nodes.Add(umlNode.Key, umlNode.Value);
            umlNodeFound = true;
        }

        if (!umlNodeFound)
        {
            return data;
        }

        sb.Append(data.Substring(start));

        return sb.ToString();
    }

    protected virtual KeyValuePair<string, string> ParseUmlNode(string startTag, string data)
    {
        var index = data.IndexOf(Environment.NewLine);
        var firstLine = index == -1 ? data : data.Substring(0, index);
        var name = firstLine.Replace(startTag, "").Trim() + Guid.NewGuid().ToString().Substring(0, 8);

        return new KeyValuePair<string, string>(name, data);
    }

    public void Dispose()
    {

    }
}

public static class DocumentHelper
{
    public static Document GetDocument(this ITextBuffer buffer)
    {
        return buffer.Properties.GetOrCreateSingletonProperty(() =>
        {
            var doc = new Document();

            doc.GetText = () =>
            {
                var temp = buffer.CurrentSnapshot.GetText();

                return temp;
            };

            doc.ParseAsync().FireAndForget();

            return doc;
        });
    }
}