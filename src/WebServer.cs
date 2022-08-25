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

        private static Encoding Encoding => Encoding.UTF8;
        protected override bool Caching => false;

        private static TimeSpan MinChunkDuration => TimeSpan.FromSeconds(1);
        private static TimeSpan TargetChunkDuration => TimeSpan.FromSeconds(5);

        private static int ConversionProcessors => Math.Min(8, Environment.ProcessorCount);
        private static TimeSpan ConvertTimeout => TimeSpan.FromSeconds(30);

        private static string DefaultChunkDirectory => GetSystemPath("chunks");

        private static string[] ValidContent => new [] { "at", "sb" };

        private readonly Engine engine = new(GetSystemPath("ffmpeg/ffmpeg.exe"));
        private readonly Dictionary<string, Queue<MediaFile>> registry = new();
        private readonly Random random = new(Environment.TickCount);

        private bool running;
        private readonly string contentDirectory;

        private MediaFile latestChunk;

        public new static ICommandArg[] Args => JapeService.Service.Args.Concat(new ICommandArg[]
        {
            CommandArg<string>.CreateOptional("--content", "<string> Path to the directory that holds the content"),
        }).ToArray();

        public WebServer(int http, int https, string contentPath) : base(http, https)
        {
            contentDirectory = contentPath;
        }

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
            running = true;
            Stream stream = new(GenerateNext);
            await MainLoop(stream);
        }

        protected override void OnStop()
        {
            running = false;
        }

        private async Task MainLoop(Stream stream)
        {
            while (running)
            {
                InputFile file = await stream.Next();

                foreach (Queue<MediaFile> queue in registry.Values)
                {
                    queue.Enqueue(file);
                }

                latestChunk = file;

                Log.Write($"Broadcast chunk: {file.Label()}");

                await Task.Delay(file.MetaData.Duration);
            }
        }

        private async IAsyncEnumerator<InputFile> GenerateNext()
        {
            Log.Write("Generating next video...");

            InputFile input = GetNextVideo(contentDirectory);

            Log.Write($"Loading video: {input.Label()}");

            Guid id = Guid.NewGuid();
            Log.Write($"Fragmenting video: {id}");
            await foreach (InputFile file in FragmentVideo(id, input))
            {
                yield return file;
            }

            Log.Write($"Fragmenting complete: {id}");
        }

        private InputFile GetNextVideo(string contentDirectory)
        {
            string[] fileChoices;
            do
            {
                string[] directoryChoices = Directory.GetDirectories(contentDirectory)
                                                     .Where(directory => ValidContent.Any(directory.EndsWith))
                                                     .ToArray();

                if (directoryChoices.Length <= 0)
                {
                    Log.Write($"Content directory is empty: {contentDirectory}");
                    return null;
                }

                int directoryIndex = random.Next(directoryChoices.Length);
                string selectedDirectory = directoryChoices[directoryIndex];

                Log.Write($"Searching Directory files: {selectedDirectory}");

                fileChoices = Directory.EnumerateFiles(selectedDirectory, "*.*", SearchOption.AllDirectories).ToArray();
                if (fileChoices.Length <= 0)
                {
                    Log.Write($"Selected directory does not contain any files: {selectedDirectory}");
                }
                
            } while (fileChoices.Length <= 0);

            int fileIndex = random.Next(fileChoices.Length);
            string selectedFile = fileChoices[fileIndex];

            return new InputFile(selectedFile);
        }

        private async IAsyncEnumerable<InputFile> FragmentVideo(Guid id, InputFile input)
        {
            await engine.GetMetaDataAsync(input, CancellationToken.None);
            TimeSpan duration = input.MetaData.Duration;

            List<Chunk> chunks = new();
            for (int i = 0; (i * TargetChunkDuration) + MinChunkDuration < duration; i++)
            {
                string path = Path.Combine(DefaultChunkDirectory, id.ToString());

                Directory.CreateDirectory(path);

                OutputFile output = new(Path.Combine(path, $"{i}.{VIDEO_FORMAT}"));

                TimeSpan chunkStart = TargetChunkDuration * i;
                TimeSpan chunkDuration = TargetChunkDuration;

                Chunk chunk = new(engine, chunkStart, chunkDuration, output);
                chunks.Add(chunk);
            }

            for (int i = 0; i < chunks.Count; i += ConversionProcessors)
            {
                Index start = i;
                Index end = i + ConversionProcessors;
                Chunk[] chunkBatch = chunks.Take(new Range(start, end)).ToArray();
                Task<InputFile>[] processors = chunkBatch.Select(chunk => chunk.Convert(input, new ConversionOptions
                {
                    VideoFormat = VIDEO_FORMAT,
                    ExtraArguments = $"-cpu-used -5 -deadline realtime -threads {Environment.ProcessorCount}"
                })).ToArray();

                Task<InputFile[]> convert = Task.WhenAll(processors);

                InputFile[] files = null;

                try
                {
                    files = await convert;
                }
                catch (Exception)
                {
                    if (convert.Exception != null)
                    {
                        foreach (Exception exception in convert.Exception.InnerExceptions)
                        {
                            Log.Write(exception.Message);
                        }
                    }
                }

                foreach (InputFile file in files)
                {
                    yield return file;
                }
            }
        }

        private static string GetSystemPath(string path) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SystemPath.Format(path));

        public class Chunk
        {
            private readonly Engine engine;
            private readonly TimeSpan start;
            private readonly TimeSpan duration;
            private readonly OutputFile output;

            public Chunk(Engine engine, TimeSpan start, TimeSpan duration, OutputFile output)
            {
                this.engine = engine;
                this.start = start;
                this.duration = duration;
                this.output = output;
            }

            public async Task<InputFile> Convert(InputFile input, ConversionOptions options)
            {
                options.CutMedia(start, duration);

                MediaFile file;
                try
                {
                    file = await engine.ConvertAsync(input, output, options, CancellationToken.None).WaitAsync(ConvertTimeout);
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException($"{exception.Message} Convert '{input.FileInfo.Name}': {start} - {start + duration}");
                }
                InputFile chunk = new(file.FileInfo.FullName);
                await engine.GetMetaDataAsync(chunk, CancellationToken.None);

                Log.Write($"Chunk '{start} - {start + duration}': {chunk.Label()}");

                return chunk;
            }
        }

        public class Stream
        {
            private const int CACHE_SIZE = 10;

            private readonly Queue<InputFile> queue = new();
            private IAsyncEnumerator<InputFile> enumerator;

            private bool running;

            public Stream(Func<IAsyncEnumerator<InputFile>> generator)
            {
                enumerator = GetEnumerator(generator);
            }

            public void Queue(InputFile file)
            {
                queue.Enqueue(file);
            }

            public async Task<InputFile> Next()
            {
                if (queue.Count <= 0)
                {
                    Log.Write("Warning: Stream ran out of chunks");

                    await GenerateData();

                    return queue.Dequeue();
                }

                if (!running && queue.Count < CACHE_SIZE)
                {
                    #pragma warning disable CS4014
                    GenerateData();
                    #pragma warning restore CS4014
                }

                return queue.Dequeue();
            }

            public async Task GenerateData()
            {
                if (running) { Log.Write("Warning: Generator already running"); return; }
                running = true;

                await enumerator.MoveNextAsync();
                Queue(enumerator.Current);

                #pragma warning disable CS4014
                Task.Run(async () =>
                {
                    while (queue.Count <= CACHE_SIZE)
                    {
                        await enumerator.MoveNextAsync();
                        Queue(enumerator.Current);
                    } 
                    running = false;
                });
                #pragma warning restore CS4014
            }

            public async IAsyncEnumerator<InputFile> GetEnumerator(Func<IAsyncEnumerator<InputFile>> generator)
            {
                while (true) {
                    IAsyncEnumerator<InputFile> enumerator = generator();
                    while (await enumerator.MoveNextAsync())
                    {
                        yield return enumerator.Current;
                    }
                    await enumerator.DisposeAsync();
                }
            }
        }
    }
}
