const BUFFER_MODE = "sequence"
const EVENT_MEDIA_SOURCE_OPEN = "sourceopen";
const EVENT_SOURCE_BUFFER_IDLE = "updateend";

const CODEC = `video/webm; codecs="vp9, opus"`;

const BUFFER_LOOKAHEAD_SECONDS = 30;
const BUFFER_TAIL_SECONDS = 10;

const TICK_RATE = 1000;

const video = document.getElementById("player");
const dispatch = dispatcher();

let media;
let source;
let clientId;

main();

function main() {
  media = new MediaSource();

  if (!("MediaSource" in window) || !MediaSource.isTypeSupported(CODEC)) {
    console.error("Unsupported codec: ", CODEC);
    return;
  }

  video.src = URL.createObjectURL(media);
  media.addEventListener(EVENT_MEDIA_SOURCE_OPEN, onMediaSourceOpen);
}

async function onMediaSourceOpen() {
  source = media.addSourceBuffer(CODEC);
  source.mode = BUFFER_MODE;

  clientId = await register();

  mediaRunner();

  source.addEventListener(EVENT_SOURCE_BUFFER_IDLE, onBufferIdle);
  await onBufferIdle();
}

async function onBufferIdle() {
  if (dispatch.tick()) { return; }
  await setTimeout(() => {
    onBufferIdle();
  }, TICK_RATE);
}

function mediaRunner() {
  setInterval(async () => {
    const remainingTime = video.duration - video.currentTime;
    if (Number.isNaN(remainingTime) || remainingTime < BUFFER_LOOKAHEAD_SECONDS) {
      const data = await getNextChunk();
      dispatch.queue(() => buffer(data));
    }
  }, TICK_RATE);
}

function dispatcher() {
  const actions = [];

  function tick() {
    if (!source) { return; }
    if (source.updating !== false) { return; }
    if (media.readyState !== "open") { return; }

    const next = actions.shift();
    if (next) {
      return next();
    }
    return false;
  }

  function queue(action) {
    actions.push(action);
  }

  return {
    tick,
    queue,
  };
}

function buffer(data) {
  if (!data) { return false; }
  if (data.byteLength <= 0) { return false; }

  source.appendBuffer(data);
  dispatch.queue(cutExcess);

  video.play();

  return true;
}

function cutExcess() {
  const threshold = BUFFER_TAIL_SECONDS;
  const currentTime = video.currentTime;

  if (video.currentTime - threshold < 0) { return false; }

  source.remove(0, currentTime - threshold);

  return true;
}

async function register() {
  const response = await request("register");
  return response.text();
}

async function getNextChunk() {
  const response = await request("next", clientId, {
    headers: {
      "Content-Type": "application/octet-stream",
    }
  });
  return response.arrayBuffer();
}

async function request(path, data, args = {}) {
  return await fetch(path, {
    method: "POST",
    body: data,
    ...args,
  });
}