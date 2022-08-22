using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.NET;
using FFmpeg.NET.Enums;
using FFmpeg.NET.Events;
using JapeCore;
using JapeHttp;
using JapeWeb;
using Microsoft.Extensions.FileProviders;

namespace Cujoe
{
    public class WebServer : JapeWeb.WebServer
    {
        private const VideoFormat VIDEO_FORMAT = VideoFormat.webm;

        private const int MIN_CHUNK_SECONDS = 1;
        private const int MAX_CHUNK_SECONDS = 10;

        private const int POLLING_RATE = 100;

        private static Encoding Encoding => Encoding.UTF8;

        protected override bool Caching => false;

        private bool running = true;
        private bool converting;

        private readonly Engine engine = new(GetSystemPath("ffmpeg/ffmpeg.exe"));
        private readonly Dictionary<string, Queue<MediaFile>> registry = new();

        public WebServer(int http, int https) : base(http, https) {}

        protected override IEnumerator<WebComponent> Components() 
        {
            yield return Use(Register);
            yield return Use(Next);

        }

        private async Task<Middleware.Result> Register(Middleware.Request request)
        {
            if (!request.Path.StartsWithSegments("/register"))
            {
                return request.Next();
            }

            string clientId = Guid.NewGuid().ToString();
            registry.Add(clientId, new Queue<MediaFile>());
            return await request.Complete(Status.SuccessCode.Accepted, clientId);
        }

        private async Task<Middleware.Result> Next(Middleware.Request request)
        {
            if (!request.Path.StartsWithSegments("/next"))
            {
                return request.Next();
            }

            if (!registry.TryGetValue(request.Data, out Queue<MediaFile> queue))
            {
                return await request.Abort(Status.ErrorCode.Forbidden);
            }

            if (queue.Count <= 0)
            {
                return await request.Complete(Status.SuccessCode.Empty);
            }

            MediaFile chunk = queue.Dequeue();

            byte[] data = chunk.FileInfo.OpenRead().ReadBytes(Encoding);

            Log.Write($"Send chunk: {chunk.FileInfo.Name}");

            return await request.Complete(Status.SuccessCode.Ok, data, Encoding);
        }

        protected override async Task OnStartAsync()
        {
            while (running)
            {
                await MainLoop();
            }
        }

        protected override void OnStop()
        {
            running = false;
        }

        private async Task MainLoop()
        {
            InputFile video = GetNext();
            Guid id = Guid.NewGuid();
            InputFile[] chunks = await GetVideoChunks(id, video);

            foreach (InputFile chunk in chunks)
            {
                await engine.GetMetaDataAsync(chunk, CancellationToken.None);
                TimeSpan duration = chunk.MetaData.Duration;
                foreach (Queue<MediaFile> queue in registry.Values)
                {
                    queue.Enqueue(chunk);
                }
                await Task.Delay(duration);
            }
        }

        private async Task<InputFile[]> GetVideoChunks(Guid id, InputFile input)
        {
            await engine.GetMetaDataAsync(input, CancellationToken.None);
            TimeSpan duration = input.MetaData.Duration;

            List<InputFile> chunks = new();
            for (int i = 0; (i * MAX_CHUNK_SECONDS) + MIN_CHUNK_SECONDS < duration.TotalSeconds; i++)
            {
                string path = GetSystemPath($"private/content/chunks/{id}");

                Directory.CreateDirectory(path);

                OutputFile output = new($"{path}{Path.DirectorySeparatorChar}{i}.{VIDEO_FORMAT}");

                TimeSpan chunkStart = TimeSpan.FromSeconds(i * MAX_CHUNK_SECONDS);
                TimeSpan chunkDuration = TimeSpan.FromSeconds(MAX_CHUNK_SECONDS);

                ConversionOptions options = new();
                options.CutMedia(chunkStart, chunkDuration);
                options.VideoFormat = VIDEO_FORMAT;
                options.ExtraArguments = $"-cpu-used -5 -deadline realtime -threads {Environment.ProcessorCount}";

                converting = true;
                engine.Complete += OnConvertComplete;

                MediaFile chunk = await engine.ConvertAsync(input, output, options, CancellationToken.None);

                await Wait.Until(() => !converting, TimeSpan.FromMilliseconds(POLLING_RATE), CancellationToken.None);

                chunks.Add(new InputFile(chunk.FileInfo.FullName));

                engine.Complete -= OnConvertComplete;
            }

            return chunks.ToArray();
        }

        private void OnConvertComplete(object sender, ConversionCompleteEventArgs e)
        {
            converting = false;
        }

        private InputFile GetNext()
        {
            return new InputFile(GetSystemPath("private/content/bunny/frag_bunny.mp4"));
        }

        private static string GetSystemPath(string path) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SystemPath.Format(path));
    }
}
