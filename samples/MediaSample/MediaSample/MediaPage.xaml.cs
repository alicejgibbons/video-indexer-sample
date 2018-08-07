using Plugin.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Plugin.Media.Abstractions;
using Xamarin.Forms;

namespace MediaSample
{
	public partial class MediaPage : ContentPage
	{
		private readonly IVideoIndexerClient client;
		public MediaPage(IVideoIndexerClient client)
		{
			this.client = client;
			InitializeComponent();

			takeVideo.Clicked += async (sender, args) =>
			{
				if (!CrossMedia.Current.IsCameraAvailable || !CrossMedia.Current.IsTakeVideoSupported)
				{
					DisplayAlert("No Camera", ":( No camera avaialble.", "OK");
					return;
				}

				var file = await CrossMedia.Current.TakeVideoAsync(new StoreVideoOptions
				{
					Quality = VideoQuality.Low,
					DesiredLength = TimeSpan.FromHours(1d).Subtract(TimeSpan.FromSeconds(1d)),
					DefaultCamera = CameraDevice.Rear,
				    Name = $"{DateTime.Now.Year}-{DateTime.Now.Month}-{DateTime.Now.Day}-{DateTime.Now.Hour}-{DateTime.Now.Minute}-{DateTime.Now.Second}.mp4",
					Directory = "DefaultVideos",
				});

				if (file == null)
				{
					return;
				}

				var path = file.Path;
				file.Dispose();

				await client.UploadAsync(path, apiKey.Text,storageCS.Text, viAccountName.Text, status => { statusLable.Text = status; });
			};

			pickVideo.Clicked += async (sender, args) =>
			{
				if (!CrossMedia.Current.IsPickVideoSupported)
				{
					DisplayAlert("Videos Not Supported", ":( Permission not granted to videos.", "OK");
					return;
				}
				var file = await CrossMedia.Current.PickVideoAsync();

				if (file == null)
				{
					return;
				}

				var path = file.Path;
				file.Dispose();
				
				await client.UploadAsync(path, apiKey.Text, storageCS.Text, viAccountName.Text, status => { statusLable.Text = status; });
			};
		}
	}
}
