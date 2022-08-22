using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.NET;
using FFmpeg.NET.Enums;
using JapeCore;
using JapeHttp;
using JapeWeb;

namespace Cujoe
{
    public class WebServer : JapeWeb.WebServer
    {
        private const VideoFormat VIDEO_FORMAT = VideoFormat.webm;

        private static TimeSpan MinChunkDuration = TimeSpan.FromSeconds(1);
        private static TimeSpan TargetChunkDuration = TimeSpan.FromSeconds(5);
        private static TimeSpan ConvertTimeout = TimeSpan.FromSeconds(30);

        private static string ContentDirectory => GetSystemPath("private/content");
        private static string ChunkDirectory => GetSystemPath("private/chunks");

        private static Encoding Encoding => Encoding.UTF8;
        protected override bool Caching => false;

        private readonly Engine engine = new(GetSystemPath("ffmpeg/ffmpeg.exe"));
        private readonly Dictionary<string, Queue<MediaFile>> registry = new();
        private readonly Random random = new(Environment.TickCount);

        private MediaFile latestChunk;

        private bool running = true;

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
            Queue<MediaFile> clientQueue = new Queue<MediaFile>();
            if (latestChunk != null) { clientQueue.Enqueue(latestChunk); }
            registry.Add(clientId, clientQueue);
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

            Log.Write($"Send chunk: {chunk.Label()}");

            return await request.Complete(Status.SuccessCode.Ok, data, Encoding);
        }

        protected override async Task OnStartAsync()
        {
            InputFile[] nextChunks = await GenerateNext();
            while (running)
            {
                Task mainLoop = MainLoop(nextChunks);
                nextChunks = await GenerateNext();
                await mainLoop;
            }
        }

        protected override void OnStop()
        {
            running = false;
        }

        private async Task MainLoop(InputFile[] chunks)
        {
            foreach (InputFile chunk in chunks)
            {
                TimeSpan duration = chunk.MetaData.Duration;
                foreach (Queue<MediaFile> queue in registry.Values)
                {
                    queue.Enqueue(chunk);
                }

                latestChunk = chunk;

                Log.Write($"Broadcast chunk: {chunk.Label()}");

                await Task.Delay(duration);
            }
        }

        private async Task<InputFile[]> GenerateNext()
        {
            InputFile[] chunks;
            do
            {
                Log.Write("Generating next video...");
                InputFile video = GetNextVideo();
                Log.Write($"Loading video: {video.Label()}");
                chunks = await GetVideoChunks(video);
                if (!Success()) { Log.Write("Loading failed"); }
            } while (!Success());

            Log.Write("Loading successful");

            return chunks;

            bool Success() => chunks?.Length >= 0;
        }

        private InputFile GetNextVideo()
        {
            string[] directoryChoices = Directory.GetDirectories(ContentDirectory);

            if (directoryChoices.Length <= 0)
            {
                Log.Write($"Content directory is empty: {ContentDirectory}");
                return null;
            }

            int directoryIndex = random.Next(directoryChoices.Length);
            string selectedDirectory = directoryChoices[directoryIndex];

            string[] fileChoices = Directory.GetFiles(selectedDirectory);

            if (fileChoices.Length <= 0)
            {
                Log.Write($"Selected directory does not contain any files: {selectedDirectory}");
                return null;
            }

            int fileIndex = random.Next(fileChoices.Length);
            string selectedFile = fileChoices[fileIndex];

            return new InputFile(selectedFile);
        }

        private async Task<InputFile[]> GetVideoChunks(InputFile input)
        {
            Guid id = Guid.NewGuid();
            Log.Write("Fragmenting video");
            return await FragmentVideo(id, input);
        }

        private async Task<InputFile[]> FragmentVideo(Guid id, InputFile input)
        {
            await engine.GetMetaDataAsync(input, CancellationToken.None);
            TimeSpan duration = input.MetaData.Duration;

            List<Task<InputFile>> tasks = new();
            for (int i = 0; (i * TargetChunkDuration) + MinChunkDuration < duration; i++)
            {
                string path = Path.Combine(ChunkDirectory, id.ToString());

                Directory.CreateDirectory(path);

                OutputFile output = new OutputFile(Path.Combine(path, $"{i}.{VIDEO_FORMAT}"));

                TimeSpan chunkStart = TargetChunkDuration * i;
                TimeSpan chunkDuration = TargetChunkDuration;

                try
                {
                    tasks.Add(GetChunk(input, output, chunkStart, chunkDuration, new ConversionOptions
                    {
                        VideoFormat = VIDEO_FORMAT,
                        ExtraArguments = $"-cpu-used -5 -deadline realtime -threads {Environment.ProcessorCount}"
                    }));
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException($"{exception.Message} Convert '{input.Label()}': {chunkStart} - {chunkStart + chunkDuration} took longer than {ConvertTimeout.TotalSeconds} seconds.");
                }

            }

            Task<InputFile[]> process = Task.WhenAll(tasks);

            InputFile[] chunks;
            try
            {
                chunks = await process;
            }
            catch (Exception)
            {
                if (process.Exception != null)
                {
                    foreach (Exception exception in process.Exception.InnerExceptions)
                    {
                        Log.Write(exception.Message);
                    }
                }
                return null;
            }

            return chunks;
        }

        private async Task<InputFile> GetChunk(InputFile input, OutputFile output, TimeSpan chunkStart, TimeSpan chunkDuration, ConversionOptions options)
        {
            options.CutMedia(chunkStart, chunkDuration);

            MediaFile video;
            try
            {
                video = await engine.ConvertAsync(input, output, options, CancellationToken.None).WaitAsync(ConvertTimeout);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"{exception.Message} Convert '{input.FileInfo.Name}': {chunkStart} - {chunkStart + chunkDuration}");
            }
            InputFile chunk = new InputFile(video.FileInfo.FullName);
            await engine.GetMetaDataAsync(chunk, CancellationToken.None);
            return chunk;
        }

        private static string GetSystemPath(string path) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SystemPath.Format(path));
    }
}
