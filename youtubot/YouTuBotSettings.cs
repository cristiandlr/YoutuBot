using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace youtubot
{
	public class YouTuBotSettings
	{
		public string Title { get; set; }
		public string Description { get; set; }
		public string[] Tags { get; set; }
		public string CategoryId { get; set; }
		public string PrivacyStatus { get; set; } // "private" or "public" or "unlisted"
		public string VideoFilePath { get; set; } // Replace with path to actual movie file.
		public string JsonClientSecretPath { get; set; }
		public string UserName { get; set; }
		public string VideoId { get; set; }
		public string CaptionLanguage { get; set; }
		public string CaptionFile { get; set; }
	}
}
