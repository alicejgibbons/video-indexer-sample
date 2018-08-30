using Android.App;
using Android.Content.PM;
using Android.OS;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.DataMovement;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Plugin.CurrentActivity;
using Plugin.Permissions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Xamarin.Android.Net;

namespace MediaSample.Droid
{
	[Activity(Label = "Azure Video Indexer Sample", Icon = "@drawable/icon", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation)]
	public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsApplicationActivity
	{
		protected override void OnCreate(Bundle bundle)
		{
			base.OnCreate(bundle);

			// You may use ServicePointManager here
			ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;
			ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
			ServicePointManager.DefaultConnectionLimit = 100;

			CrossCurrentActivity.Current.Init(this, bundle);

			global::Xamarin.Forms.Forms.Init(this, bundle);
			LoadApplication(new App(new AndroidVideoIndexerClient()));
		}

		public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
		{
			PermissionsImplementation.Current.OnRequestPermissionsResult(requestCode, permissions, grantResults);
		}
	}

	public class AndroidVideoIndexerClient : IVideoIndexerClient
	{
		public async Task UploadAsync(string path, string apiKey, string storageConnectionString, string viAccountName, Action<string> status)
		{
			var apiUrl = "https://api.videoindexer.ai";
			var location = "westus2";

			// create the http client
			var handler = new AndroidClientHandler();

			var client = new HttpClient(handler);

			client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);

			// obtain account information and access token
			var queryParams = CreateQueryString(

				new Dictionary<string, string>
				{
					{"generateAccessTokens", "true"},
					{"allowEdit", "true"}
				});

			var result = await client.GetAsync($"{apiUrl}/auth/{location}/Accounts?{queryParams}");

			var json = await result.Content.ReadAsStringAsync();
			var accounts = JsonConvert.DeserializeObject<AccountContractSlim[]>(json);
			// take the relevant account, here we simply take the first
			var accountInfo = accounts.First(a => a.Name.ToLower() == viAccountName);

			// we will use the access token from here on, no need for the apim key
			client.DefaultRequestHeaders.Remove("Ocp-Apim-Subscription-Key");

			// upload a video
			var content = new MultipartFormDataContent();

			var account = CloudStorageAccount.Parse(storageConnectionString);
			var blobClient = account.CreateCloudBlobClient();

			var blobContainer = blobClient.GetContainerReference("uploads");
			await blobContainer.CreateIfNotExistsAsync();

			var destBlob = blobContainer.GetBlockBlobReference(Path.GetFileName(path));
			await destBlob.DeleteIfExistsAsync();

			double length;
			using (var stream = File.Open(path, FileMode.Open))
			{
				length = Convert.ToDouble(stream.Length);
			}

			// Setup the number of the concurrent operations
			TransferManager.Configurations.ParallelOperations = 64;
			// Setup the transfer context and track the upoload progress
			var context = new SingleTransferContext();
			context.ProgressHandler = new Progress<TransferStatus>((progress) =>
			{
				status($"{Path.GetFileName(path)} {Math.Round(progress.BytesTransferred / length * 100d, 2)}% uploaded");
			});

			// Upload a local blob
			await TransferManager.UploadAsync(path, destBlob, null, context, CancellationToken.None);

			// get the video from URL
			var sharedAccessSignature = destBlob.GetSharedAccessSignature(new SharedAccessBlobPolicy
			{
				Permissions = SharedAccessBlobPermissions.Read,
				SharedAccessExpiryTime = DateTimeOffset.Now.AddHours(2d),
				SharedAccessStartTime = DateTimeOffset.Now.Subtract(TimeSpan.FromHours(2))
			});

			var videoUrl = destBlob.Uri.AbsoluteUri + sharedAccessSignature;

			queryParams = CreateQueryString(
				 new Dictionary<string, string>
				 {
					{"accessToken", accountInfo.AccessToken},
					{"name", Path.GetFileName(path)},
					{"description", ""},
					{"privacy", "private"},
					{"partition", "partition"},
					{"videoUrl", videoUrl},
				 });

			var uploadRequestResult = await client.PostAsync($"{apiUrl}/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos?{queryParams}", content);
			var uploadResult = await uploadRequestResult.Content.ReadAsStringAsync();

			// get the video id from the upload result
			var videoId = ((JValue)JsonConvert.DeserializeObject<dynamic>(uploadResult)["id"]).Value.ToString();

			status($"Video ID:{videoId}");

			if (File.Exists(path))
			{
				File.Delete(path);
			}

			// wait for the video index to finish
			while (true)
			{
				await Task.Delay(10000);

				queryParams = CreateQueryString(
					new Dictionary<string, string>
					{
						{ "accessToken", accountInfo.AccessToken},
						{"language", "English"},
					});

				var videoGetIndexRequestResult = await client.GetAsync($"{apiUrl}/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos/{videoId}/Index?{queryParams}");
				var videoGetIndexResultContent = videoGetIndexRequestResult.Content;

				if (videoGetIndexResultContent == null) continue;

				var videoGetIndexResult = await videoGetIndexResultContent.ReadAsStringAsync();

				var deserializeObject = JsonConvert.DeserializeObject<JObject>(videoGetIndexResult);
				var video = deserializeObject["videos"].First();

				var processingState = ((JValue)video["state"]).Value.ToString();
				var processingProgress = ((JValue)video["processingProgress"])?.Value.ToString();

				var failureMessage = ((JValue)video["failureMessage"])?.Value.ToString();
				var failureCode = ((JValue)video["failureCode"])?.Value.ToString();

				if (string.IsNullOrWhiteSpace(failureMessage))
				{
					status($"State:{processingState} {processingProgress}");

					// job is finished
					if (processingState != "Uploaded" && processingState != "Processing")
					{
						//status($"Full JSON:{videoGetIndexResult}");
						break;
					}
				}
				else
					status($"State:{processingState} {failureCode}:{failureMessage}");
			}

			// search for the video
			queryParams = CreateQueryString(
				new Dictionary<string, string>
				{
					{"accessToken", accountInfo.AccessToken},
					{"id", videoId},
				});

			var searchRequestResult = await client.GetAsync($"{apiUrl}/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos/Search?{queryParams}");

			var searchResult = await searchRequestResult.Content.ReadAsStringAsync();

			//status($"Search:{searchResult}");

			// Generate video access token (used for get widget calls)
			client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);

			var videoTokenRequestResult = await client.GetAsync($"{apiUrl}/auth/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos/{videoId}/AccessToken?allowEdit=true");
			var videoAccessToken = (await videoTokenRequestResult.Content.ReadAsStringAsync()).Replace("\"", "");

			client.DefaultRequestHeaders.Remove("Ocp-Apim-Subscription-Key");

			// get insights widget url
			queryParams = CreateQueryString(
				new Dictionary<string, string>
				{
					{"accessToken", videoAccessToken},
					{"widgetType", "Keywords"},
					{"allowEdit", "true"},
				});

			var insightsWidgetRequestResult = await client.GetAsync($"{apiUrl}/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos/{videoId}/InsightsWidget?{queryParams}");
			var insightsWidgetLink = insightsWidgetRequestResult.Headers.Location;

			//status($"Insights Widget url:{insightsWidgetLink}");

			// get player widget url
			queryParams = CreateQueryString(
				new Dictionary<string, string>
				{
					{"accessToken", videoAccessToken},
				});

			var playerWidgetRequestResult = await client.GetAsync($"{apiUrl}/{accountInfo.Location}/Accounts/{accountInfo.Id}/Videos/{videoId}/PlayerWidget?{queryParams}");
			var playerWidgetLink = playerWidgetRequestResult.Headers.Location;

			//status($"url:{playerWidgetLink}");

			status("ready");
		}


		private string CreateQueryString(IDictionary<string, string> parameters)
		{
			var queryParameters = HttpUtility.ParseQueryString(string.Empty);

			foreach (var parameter in parameters)
			{
				queryParameters[parameter.Key] = parameter.Value;
			}

			return queryParameters.ToString();
		}

		public class AccountContractSlim
		{
			public Guid Id { get; set; }
			public string Name { get; set; }
			public string Location { get; set; }
			public string AccountType { get; set; }
			public string Url { get; set; }
			public string AccessToken { get; set; }
		}
	}



	public class Rootobject
	{
		public string accountId { get; set; }
		public string id { get; set; }
		public string partition { get; set; }
		public string name { get; set; }
		public object description { get; set; }
		public string userName { get; set; }
		public DateTime created { get; set; }
		public string privacyMode { get; set; }
		public string state { get; set; }
		public bool isOwned { get; set; }
		public bool isEditable { get; set; }
		public bool isBase { get; set; }
		public int durationInSeconds { get; set; }
		public Summarizedinsights summarizedInsights { get; set; }
		public Video[] videos { get; set; }
		public Videosrange[] videosRanges { get; set; }
	}

	public class Summarizedinsights
	{
		public string name { get; set; }
		public string id { get; set; }
		public string privacyMode { get; set; }
		public Duration duration { get; set; }
		public string thumbnailVideoId { get; set; }
		public string thumbnailId { get; set; }
		public object[] faces { get; set; }
		public object[] keywords { get; set; }
		public object[] sentiments { get; set; }
		public object[] emotions { get; set; }
		public object[] audioEffects { get; set; }
		public object[] labels { get; set; }
		public object[] brands { get; set; }
		public Statistics statistics { get; set; }
	}

	public class Duration
	{
		public string time { get; set; }
		public int seconds { get; set; }
	}

	public class Statistics
	{
		public int correspondenceCount { get; set; }
		public Speakertalktolistenratio speakerTalkToListenRatio { get; set; }
		public Speakerlongestmonolog speakerLongestMonolog { get; set; }
		public Speakernumberoffragments speakerNumberOfFragments { get; set; }
		public Speakerwordcount speakerWordCount { get; set; }
	}

	public class Speakertalktolistenratio
	{
	}

	public class Speakerlongestmonolog
	{
	}

	public class Speakernumberoffragments
	{
	}

	public class Speakerwordcount
	{
	}

	public class Video
	{
		public string accountId { get; set; }
		public string id { get; set; }
		public string state { get; set; }
		public string moderationState { get; set; }
		public string reviewState { get; set; }
		public string privacyMode { get; set; }
		public string processingProgress { get; set; }
		public string failureCode { get; set; }
		public string failureMessage { get; set; }
		public object externalId { get; set; }
		public object externalUrl { get; set; }
		public object metadata { get; set; }
		public Insights insights { get; set; }
		public string thumbnailId { get; set; }
		public object publishedUrl { get; set; }
		public object publishedProxyUrl { get; set; }
		public string viewToken { get; set; }
		public bool detectSourceLanguage { get; set; }
		public string sourceLanguage { get; set; }
		public string language { get; set; }
		public string indexingPreset { get; set; }
		public string linguisticModelId { get; set; }
		public bool isAdult { get; set; }
	}

	public class Insights
	{
		public string version { get; set; }
		public string sourceLanguage { get; set; }
		public string language { get; set; }
	}

	public class Videosrange
	{
		public string videoId { get; set; }
		public Range range { get; set; }
	}

	public class Range
	{
		public string start { get; set; }
		public string end { get; set; }
	}


}

