
using System;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace MediaSample
{
	public interface IVideoIndexerClient
	{
		Task UploadAsync(string path, string apiKey, string storageConnectionString, string viAccountName, Action<string> status);
	}
	public class App : Application
	{
		public App(IVideoIndexerClient client)
		{
			// The root page of your application
			MainPage = new MediaPage(client);
		}

		protected override void OnStart()
		{
			// Handle when your app starts
		}

		protected override void OnSleep()
		{
			// Handle when your app sleeps
		}

		protected override void OnResume()
		{
			// Handle when your app resumes
		}
	}
}
