# Cujoe

A video streaming implementation using my [C# WebServer](https://github.com/JonathanPaugh/CSharpWebServer) project.

## Overview

I made this project because I was curious to learn about how to serve a video stream to multiple clients from a web server. It is a rough implementation, however it is fully functional.

In the future, it could probably use more features such as chunk cross-fading to fix the "zero crossing" audio issues. Also an improved catchup/slowdown mechanic to keep streams perfectly synced.

## Requirements

FFmpeg must be included in the "/src/ffmpeg" folder.

## How it works

The web server works by first selecting a video file to stream from a given content folder. This video file is then fragmented into smaller chunks using [FFmpeg](https://ffmpeg.org/) and converted to a format compatible for streamed web video. The video chunks are then loaded into the main video queue.

When a client connects it sends a request to the web server to register it, meaning it is ready to receive video chunks. The video chunk at the front of the queue is dequeued and sent to all registered clients, where it is pushed into the video buffer. The web server then waits for the video chunk duration before sending the next chunk, ensuring all clients stay in sync.

## Usage

1. Set the `ValidContent` property in `WebServer` to the content directories to stream from.

Example:

We have 2 folders to stream from:

- /content/vacation
- /content/family

We can set the valid content to select from these folders.

`ValidContent => new [] { "vacation", "family" };`

Then we provide the base "content" folder to the program's content arg when running it.

`--content "/content"`

2. Build the server and run it locally.

`Cujoe.exe --http <port> --content <insert-content-path>`
