﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MimeKit;
using MimeKit.Text;

namespace MailExtractor
{
    #region HtmlPreviewVisitor
    /// <summary>
    /// Visits a MimeMessage and generates HTML suitable to be rendered by a browser control.
    /// </summary>
    class HtmlPreviewVisitor : MimeVisitor
    {
        List<MultipartRelated> stack = new List<MultipartRelated>();
        List<MimeEntity> attachments = new List<MimeEntity>();
        readonly string inlineImageDir;
        readonly string inlineImageRelativeUrl;
        string body;

        /// <summary>
        /// Creates a new HtmlPreviewVisitor.
        /// </summary>
        /// <param name="inlineImageDirectory">A temporary directory used for storing image files.</param>
        public HtmlPreviewVisitor(string inlineImageDir, string inlineImageRelativeUrl)
        {
            this.inlineImageDir = inlineImageDir;
            this.inlineImageRelativeUrl = inlineImageRelativeUrl;
        }

        /// <summary>
        /// The list of attachments that were in the MimeMessage.
        /// </summary>
        public IList<MimeEntity> Attachments
        {
            get { return attachments; }
        }

        /// <summary>
        /// The HTML string that can be set on the BrowserControl.
        /// </summary>
        public string HtmlBody
        {
            get { return body ?? string.Empty; }
        }

        protected override void VisitMultipartAlternative(MultipartAlternative alternative)
        {
            // walk the multipart/alternative children backwards from greatest level of faithfulness to the least faithful
            for (int i = alternative.Count - 1; i >= 0 && body == null; i--)
                alternative[i].Accept(this);
        }

        protected override void VisitMultipartRelated(MultipartRelated related)
        {
            var root = related.Root;

            // push this multipart/related onto our stack
            stack.Add(related);

            // visit the root document
            root.Accept(this);

            // pop this multipart/related off our stack
            stack.RemoveAt(stack.Count - 1);
        }

        // look up the image based on the img src url within our multipart/related stack
        bool TryGetImage(string url, out MimePart image)
        {
            UriKind kind;
            int index;
            Uri uri;

            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                kind = UriKind.Absolute;
            else if (Uri.IsWellFormedUriString(url, UriKind.Relative))
                kind = UriKind.Relative;
            else
                kind = UriKind.RelativeOrAbsolute;

            try
            {
                uri = new Uri(url, kind);
            }
            catch
            {
                image = null;
                return false;
            }

            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if ((index = stack[i].IndexOf(uri)) == -1)
                    continue;

                image = stack[i][index] as MimePart;
                return image != null;
            }

            image = null;

            return false;
        }

        string GetImageExtension(MimePart image)
        {
            if (image == null || image.ContentType == null || string.IsNullOrWhiteSpace(image.ContentType.MediaSubtype))
            {
                return string.Empty;
            }

            switch (image.ContentType.MediaSubtype.ToLowerInvariant())
            {
                case "jpeg": return ".jpg";
                case "jpg": return ".jpg";
                case "png": return ".png";
                case "gif": return ".gif";
                case "bmp": return ".bmp";
                case "x-windows-bmp": return ".bmp";
                case "vnd.microsoft.icon": return ".ico";
                case "tiff": return ".tiff";
            }
            return string.Empty;
        }

        // Save the image to our temp directory and return a "file://" url suitable for
        // the browser control to load.
        // Note: if you'd rather embed the image data into the HTML, you can construct a
        // "data:" url instead.
        string SaveImage(MimePart image, string url)
        {
            string fileName = url.GetHashCode().ToString("x8", CultureInfo.InvariantCulture) + GetImageExtension(image);
            string path = Path.Combine(inlineImageDir, fileName);
            using (var output = File.Create(path))
                image.ContentObject.DecodeTo(output);

            return inlineImageRelativeUrl + fileName;
        }

        // Replaces <img src=...> urls that refer to images embedded within the message with
        // "file://" urls that the browser control will actually be able to load.
        void HtmlTagCallback(HtmlTagContext ctx, HtmlWriter htmlWriter)
        {
            if (ctx.TagId == HtmlTagId.Image && !ctx.IsEndTag && stack.Count > 0)
            {
                ctx.WriteTag(htmlWriter, false);

                // replace the src attribute with a file:// URL
                foreach (var attribute in ctx.Attributes)
                {
                    if (attribute.Id == HtmlAttributeId.Src)
                    {
                        MimePart image;
                        string url;

                        if (!TryGetImage(attribute.Value, out image))
                        {
                            htmlWriter.WriteAttribute(attribute);
                            continue;
                        }

                        url = SaveImage(image, attribute.Value);

                        htmlWriter.WriteAttributeName(attribute.Name);
                        htmlWriter.WriteAttributeValue(url);
                    }
                    else
                    {
                        htmlWriter.WriteAttribute(attribute);
                    }
                }
            }
            else if (ctx.TagId == HtmlTagId.Body && !ctx.IsEndTag)
            {
                ctx.WriteTag(htmlWriter, false);

                // add and/or replace oncontextmenu="return false;"
                foreach (var attribute in ctx.Attributes)
                {
                    if (attribute.Name.ToLowerInvariant() == "oncontextmenu")
                        continue;

                    htmlWriter.WriteAttribute(attribute);
                }

                htmlWriter.WriteAttribute("oncontextmenu", "return false;");
            }
            else if (ctx.TagId == HtmlTagId.A && !ctx.IsEndTag)
            {
                ctx.WriteTag(htmlWriter, false);

                // add and/or replace target="_blank"
                foreach (var attribute in ctx.Attributes)
                {
                    if (attribute.Name.ToLowerInvariant() == "target")
                        continue;

                    htmlWriter.WriteAttribute(attribute);
                }

                htmlWriter.WriteAttribute("target", "_blank");
            }
            else
            {
                // pass the tag through to the output
                ctx.WriteTag(htmlWriter, true);
            }
        }

        protected override void VisitTextPart(TextPart entity)
        {
            TextConverter converter;

            if (body != null)
            {
                // since we've already found the body, treat this as an attachment
                attachments.Add(entity);
                return;
            }

            if (entity.IsHtml)
            {
                converter = new HtmlToHtml {
                    HtmlTagCallback = HtmlTagCallback,
                    FilterHtml = true // remove script tag
                };
            }
            else if (entity.IsFlowed)
            {
                var flowed = new FlowedToHtml();
                string delsp;

                if (entity.ContentType.Parameters.TryGetValue("delsp", out delsp))
                    flowed.DeleteSpace = delsp.ToLowerInvariant() == "yes";

                converter = flowed;
            }
            else
            {
                converter = new TextToHtml();
            }

            body = converter.Convert(entity.Text);
        }

        /*
        protected override void VisitTnefPart(TnefPart entity)
        {
            // extract any attachments in the MS-TNEF part
            attachments.AddRange(entity.ExtractAttachments());
        }*/

        protected override void VisitMessagePart(MessagePart entity)
        {
            // treat message/rfc822 parts as attachments
            attachments.Add(entity);
        }

        protected override void VisitMimePart(MimePart entity)
        {
            // realistically, if we've gotten this far, then we can treat this as an attachment
            // even if the IsAttachment property is false.
            attachments.Add(entity);
        }
    }
    #endregion
}