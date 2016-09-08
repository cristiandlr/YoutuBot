using System;
using System.Configuration;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Linq;

namespace youtubot
{
	internal class UploadVideo
	{

		[STAThread]
		static void Main(string[] args)
		{
			Console.WriteLine("YouTuBot: Upload Video");
			Console.WriteLine("==============================");

			// Load configuration settings
			var conf = new YouTuBotSettings();
			LoadConfig(ref conf, args);

			try
			{
				new UploadVideo().Run(conf).Wait();
			}
			catch (AggregateException ex)
			{
				foreach (var e in ex.InnerExceptions)
				{
					Console.WriteLine("Error: " + e.Message);
				}
			}

			//Console.WriteLine("Press any key to continue...");
			//Console.ReadKey();
		}

		private static void LoadConfig(ref YouTuBotSettings conf, string[] args)
		{
			if (conf == null)
				conf = new YouTuBotSettings();

			// Read config file
			conf.JsonClientSecretPath = ConfigurationManager.AppSettings["JsonClientSecretPath"];
			conf.UserName = ConfigurationManager.AppSettings["UserName"];
			conf.CaptionLanguage = ConfigurationManager.AppSettings["CaptionLanguage"];

			// Get cmd parameters
			if (args.Length < 0)
				return;

			// Read cmd parameters
			int i = 0;
			int p = 0;
			foreach (var arg in args)
			{
				switch (arg)
				{
					case "--title":
						p++;
						conf.Title = args[i + 1];
						conf.Title = conf.Title.Replace("\\n", "\n");
						break;
					case "--captionFile":
						p++;
						conf.CaptionFile = args[i + 1];
						break;
					case "--description":
						p++;
						conf.Description = args[i + 1];
						conf.Description = conf.Description.Replace("\\n", "\n");
						break;
					case "--tags":
						p++;
						conf.Tags = args[i + 1].Split(',');
						break;
					case "--categoryId":
						p++;
						conf.CategoryId = args[i + 1];
						break;
					case "--privacyStatus":
						p++;
						conf.PrivacyStatus = args[i + 1];
						break;
					case "--videoFilePath":
						p++;
						conf.VideoFilePath = args[i + 1];
						if (!File.Exists(conf.VideoFilePath))
						{
							Console.WriteLine(string.Format("The file {0} was not found.", conf.VideoFilePath));
							System.Environment.Exit(0);
						}
						break;
					case "--help":
						var help = @"
YouTuBot.exe [params]

--title ""<title of video, mandatory>""
--captionFile ""<path to Caption/Subtitle file, optional>""
--description ""<description of the video, mandatory>""
--tags ""<comma-separated tags for the video, mandatory>""
--categoryId <Id of the category, mandatory>
--privacyStatus <private|public, mandatory>
--videoFilePath ""<path to the video file, mandatory>""
--help: Shows this message, optional.
Hint: use \\n for new line in title or description parameters.
Note: Put Thumbnail file (if any) sharing video directory and name, using format JPG.";
						Console.WriteLine(help);
						System.Environment.Exit(0);
						break;
				}
				i++;
			}

			if (p < 6)
			{
				Console.WriteLine("Missing parameters. Use YouTuBot.exe --help for help");
				System.Environment.Exit(0);
			}
		}

		private async Task Run(YouTuBotSettings config)
		{
			UserCredential credential;

			if (!File.Exists(config.JsonClientSecretPath))
			{
				Console.WriteLine("Secret JSON file was not found.");
				Environment.Exit(0);
			}

			using (var stream = new FileStream(config.JsonClientSecretPath, FileMode.Open, FileAccess.Read))
			{
				credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
					GoogleClientSecrets.Load(stream).Secrets,
					// This OAuth 2.0 access scope allows an application to upload files to the
					// authenticated user's YouTube channel, but doesn't allow other types of access.
					new [] {
						YouTubeService.Scope.Youtube,
						YouTubeService.Scope.YoutubeForceSsl, 
						YouTubeService.Scope.Youtubepartner,
						YouTubeService.Scope.YoutubepartnerChannelAudit,
						YouTubeService.Scope.YoutubeReadonly,
						YouTubeService.Scope.YoutubeUpload
					},
					config.UserName, //"user",
					CancellationToken.None
				);
			}

			var youtubeService = new YouTubeService(new BaseClientService.Initializer()
			{
				HttpClientInitializer = credential,
				ApplicationName = Assembly.GetExecutingAssembly().GetName().Name
			});

			config.VideoId = await UploadYtVideo(config, youtubeService);

			UploadCaption(config, youtubeService);

			UploadThumbnail(config, youtubeService);
		}

		private async Task<string> UploadYtVideo(YouTuBotSettings config, YouTubeService youtubeService)
		{
			var video = new Video();
			video.Snippet = new VideoSnippet();
			video.Snippet.Title = config.Title;
			video.Snippet.Description = config.Description;
			video.Snippet.Tags = config.Tags;
			video.Snippet.CategoryId = config.CategoryId; //"22"; // See https://developers.google.com/youtube/v3/docs/videoCategories/list
			video.Status = new VideoStatus();
			video.Status.PrivacyStatus = config.PrivacyStatus; // or "private" or "public" or "unlisted"
			var filePath = config.VideoFilePath; // Replace with path to actual movie file.

			var vidRes = new VideosResource(youtubeService);

			const int KB = 0x400;
			var minimumChunkSize = 256 * KB;

			using (var fileStream = new FileStream(filePath, FileMode.Open))
			{
				var videosInsertRequest = vidRes.Insert(video, "snippet,status", fileStream, "video/*");
				videosInsertRequest.ProgressChanged += videosInsertRequest_ProgressChanged;
				videosInsertRequest.ResponseReceived += videosInsertRequest_ResponseReceived;
				videosInsertRequest.ChunkSize = minimumChunkSize;
				videosInsertRequest.NotifySubscribers = true;

				var progress = await videosInsertRequest.UploadAsync();

				config.VideoId = videosInsertRequest.ResponseBody.Id;
			}

			return config.VideoId;
		}


		/// <summary>
		/// Uploads a caption track in draft status that matches the API request parameters. 
		/// (captions.insert)
		/// </summary>
		/// <param name="config"></param>
		/// <param name="youtubeService"></param>
		private void UploadCaption(YouTuBotSettings config, YouTubeService youtubeService)
		{
			string captionFile = config.CaptionFile;

			if (!File.Exists(captionFile))
				return; //Ignore subtitles file when not found.

			// Add extra information to the caption before uploading.
			var captionObjectDefiningMetadata = new Caption();
			//captionObjectDefiningMetadata.Kind = "youtube#caption";
			captionObjectDefiningMetadata.Snippet = new CaptionSnippet();
			captionObjectDefiningMetadata.Snippet.VideoId = config.VideoId;
			captionObjectDefiningMetadata.Snippet.Language = config.CaptionLanguage;
			captionObjectDefiningMetadata.Snippet.Name = config.CaptionFile;
			//captionObjectDefiningMetadata.Snippet.IsDraft = false;
			//captionObjectDefiningMetadata.Snippet.TrackKind = "standard";

			var capRes = new CaptionsResource(youtubeService);

			var minimumChunkSize = 8 * Google.Apis.Upload.ResumableUpload<int>.MinimumChunkSize;

			using (var mediaContent = new FileStream(captionFile, FileMode.Open))
			{
				var part = "snippet";
				// Create an API request that specifies that the mediaContent object is the caption of the specified video.
				var captionInsertRequest = capRes.Insert(captionObjectDefiningMetadata, part, mediaContent, "*/*");
				
				captionInsertRequest.ProgressChanged += captionInsertRequest_ProgressChanged;
				captionInsertRequest.ResponseReceived += captionInsertRequest_ResponseReceived;
				captionInsertRequest.ChunkSize = minimumChunkSize;
				//captionInsertRequest.Sync = false;

				//var response = await captionInsertRequest.UploadAsync();
				var response = captionInsertRequest.Upload();
			}
		}

		/// <summary>
		///  Upload the image and set it as the specified video's thumbnail.
		/// </summary>
		/// <param name="config"></param>
		/// <param name="youtubeService"></param>
		private void UploadThumbnail(YouTuBotSettings config, YouTubeService youtubeService)
		{
			string thumbFile = config.VideoFilePath.Replace(".mp4", ".jpg");

			if (!File.Exists(thumbFile))
				return; //Ignore Thumbnail if JPG file desn't exist

			var thumbRes = new ThumbnailsResource(youtubeService);

			var minimumChunkSize = 32 * Google.Apis.Upload.ResumableUpload<int>.MinimumChunkSize;

			using (var mediaContent = new FileStream(thumbFile, FileMode.Open))
			{
				var mediaUpl = thumbRes.Set(config.VideoId, mediaContent, "image/jpeg");

				mediaUpl.ChunkSize = minimumChunkSize;
				mediaUpl.ProgressChanged += ThumbInsertRequest_ProgressChanged;
				mediaUpl.ResponseReceived += ThumbInsertRequest_ResponseReceived;
				var response = mediaUpl.Upload();

			}
		}

		void videosInsertRequest_ProgressChanged(Google.Apis.Upload.IUploadProgress progress)
		{
			switch (progress.Status)
			{
				case UploadStatus.Uploading:
					Console.WriteLine("{0} bytes sent.", progress.BytesSent);
					break;

				case UploadStatus.Failed:
					Console.WriteLine("An error prevented the upload from completing.\n{0}", progress.Exception);
					break;

				case UploadStatus.Completed:
					Console.WriteLine("Video Upload Completed");
					break;

			}
		}

		void videosInsertRequest_ResponseReceived(Video video)
		{
			Console.WriteLine("Video id '{0}' was successfully uploaded.", video.Id);
		}

		void captionInsertRequest_ProgressChanged(Google.Apis.Upload.IUploadProgress progress)
		{
			switch (progress.Status)
			{
				case UploadStatus.Starting:
					Console.WriteLine("Starting Caption Upload");
					break;
				case UploadStatus.Completed:
					Console.WriteLine("Caption Upload Completed");
					break;
				case UploadStatus.Uploading:
					Console.WriteLine("{0} bytes sent.", progress.BytesSent);
					break;
				case UploadStatus.Failed:
					Console.WriteLine("An error prevented the captions upload from completing.\n{0}", progress.Exception);
					break;
			}
		}

		void captionInsertRequest_ResponseReceived(Caption caption)
		{
			Console.WriteLine("Caption id '{0}' was successfully uploaded.", caption.Id);
		}

		void ThumbInsertRequest_ProgressChanged(Google.Apis.Upload.IUploadProgress progress)
		{
			switch (progress.Status)
			{
				case UploadStatus.Starting:
					Console.WriteLine("Starting Thumbnail Upload");
					break;
				case UploadStatus.Completed:
					Console.WriteLine("Thumbnail Upload Completed");
					break;
				case UploadStatus.Uploading:
					Console.WriteLine("{0} bytes sent.", progress.BytesSent);
					break;
				case UploadStatus.Failed:
					Console.WriteLine("An error prevented the thumbnail upload from completing.\n{0}", progress.Exception);
					break;
			}
		}

		void ThumbInsertRequest_ResponseReceived(ThumbnailSetResponse thumb)
		{
			Console.WriteLine("Thumbnail was successfully uploaded.");
		}
	}
}