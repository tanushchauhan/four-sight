// Reads the gyroscope and accelerometer natively at their fastest rate on a dedicated thread, and
// buffers every sample with its hardware timestamp until Unity drains it (NativeImu.cs). The Input
// System delivers these sensors at Android's default rate (50 Hz on a Pixel 9 Pro) stamped with
// their arrival time, which is too coarse for the head tracker's sensor fusion.
//
// Follows the Cardboard SDK's Android sensor readers (sdk/sensors/android/, Apache License 2.0),
// which likewise prefer the uncalibrated gyroscope because the fusion estimates the bias itself.
#if defined(__ANDROID__)

#include <android/looper.h>
#include <android/sensor.h>
#include <dlfcn.h>
#include <time.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <mutex>
#include <string>
#include <thread>

namespace {

constexpr int kLooperId = 3;  // LOOPER_ID_USER, as in the SDK.
constexpr int kGyroscopeUncalibrated = 16;  // ASENSOR_TYPE_GYROSCOPE_UNCALIBRATED
// Android 12+ caps apps without HIGH_SAMPLING_RATE_SENSORS at 200 Hz.
constexpr int32_t kCappedPeriodUs = 5000;
constexpr int kPollTimeoutMs = 100;
constexpr int kCapacity = 4096;  // Seconds of samples at the fastest rates.

// Matches NativeImu.Sample in C#.
struct Sample {
  int32_t type;  // 0 gyroscope (rad/s), 1 accelerometer (m/s^2), both in the phone's frame.
  int32_t reserved;
  int64_t timestamp_ns;  // CLOCK_BOOTTIME, like SystemClock.elapsedRealtimeNanos().
  float x, y, z;
  float reserved2;
};

// libandroid is loaded at runtime, so the plugin doesn't depend on what IL2CPP links against.
struct Api {
  ASensorManager* (*getInstanceForPackage)(const char*);
  const ASensor* (*getDefaultSensor)(ASensorManager*, int);
  ASensorEventQueue* (*createEventQueue)(ASensorManager*, ALooper*, int, ALooper_callbackFunc, void*);
  int (*destroyEventQueue)(ASensorManager*, ASensorEventQueue*);
  int (*registerSensor)(ASensorEventQueue*, const ASensor*, int32_t, int64_t);
  int (*disableSensor)(ASensorEventQueue*, const ASensor*);
  ssize_t (*getEvents)(ASensorEventQueue*, ASensorEvent*, size_t);
  int (*getMinDelay)(const ASensor*);
  const char* (*getName)(const ASensor*);
  ALooper* (*prepare)(int);
  int (*pollOnce)(int, int*, int*, void**);
  bool loaded = false;

  bool Load() {
    if (loaded) return true;
    void* lib = dlopen("libandroid.so", RTLD_NOW);
    loaded = lib != nullptr &&
             Find(lib, "ASensorManager_getInstanceForPackage", &getInstanceForPackage) &&
             Find(lib, "ASensorManager_getDefaultSensor", &getDefaultSensor) &&
             Find(lib, "ASensorManager_createEventQueue", &createEventQueue) &&
             Find(lib, "ASensorManager_destroyEventQueue", &destroyEventQueue) &&
             Find(lib, "ASensorEventQueue_registerSensor", &registerSensor) &&
             Find(lib, "ASensorEventQueue_disableSensor", &disableSensor) &&
             Find(lib, "ASensorEventQueue_getEvents", &getEvents) &&
             Find(lib, "ASensor_getMinDelay", &getMinDelay) &&
             Find(lib, "ASensor_getName", &getName) &&
             Find(lib, "ALooper_prepare", &prepare) &&
             Find(lib, "ALooper_pollOnce", &pollOnce);
    return loaded;
  }

  template <typename T>
  static bool Find(void* lib, const char* name, T* function) {
    *function = reinterpret_cast<T>(dlsym(lib, name));
    return *function != nullptr;
  }
};

Api g_api;

// Guards the sample buffer and the dropped count.
std::mutex g_buffer_mutex;
Sample g_buffer[kCapacity];
int g_first = 0;
int g_count = 0;
int64_t g_dropped = 0;

// Guards starting and stopping.
std::mutex g_control_mutex;
std::atomic<bool> g_running{false};
std::thread g_thread;
std::string g_description;

// Hands the sensor thread's setup result back to CardboardXR_ImuStart.
std::mutex g_start_mutex;
std::condition_variable g_start_changed;
bool g_start_done = false;
int g_start_status = 0;

void FinishStart(int status) {
  std::lock_guard<std::mutex> lock(g_start_mutex);
  g_start_status = status;
  g_start_done = true;
  g_start_changed.notify_all();
}

// Keeps the newest samples if Unity stops draining, for example while a frame hitches.
void Push(const ASensorEvent& event) {
  int32_t type;
  if (event.type == ASENSOR_TYPE_GYROSCOPE || event.type == kGyroscopeUncalibrated) {
    type = 0;
  } else if (event.type == ASENSOR_TYPE_ACCELEROMETER) {
    type = 1;
  } else {
    return;  // Additional-info events and anything else.
  }

  if (g_count == kCapacity) {
    g_first = (g_first + 1) % kCapacity;
    g_count--;
    g_dropped++;
  }
  // The uncalibrated gyroscope's first three values are the rates without bias removed.
  Sample& sample = g_buffer[(g_first + g_count) % kCapacity];
  sample = {type, 0, event.timestamp, event.data[0], event.data[1], event.data[2], 0};
  g_count++;
}

// Registers at the sensor's fastest rate, or 200 Hz if Android refuses that. Returns the sampling
// period in microseconds, or -1.
int32_t Register(ASensorEventQueue* queue, const ASensor* sensor) {
  const int32_t fastest = std::max(g_api.getMinDelay(sensor), 1);
  if (g_api.registerSensor(queue, sensor, fastest, 0) == 0) return fastest;
  const int32_t capped = std::max(fastest, kCappedPeriodUs);
  return g_api.registerSensor(queue, sensor, capped, 0) == 0 ? capped : -1;
}

void Run(std::string package_name) {
  ALooper* looper = g_api.prepare(ALOOPER_PREPARE_ALLOW_NON_CALLBACKS);
  ASensorManager* manager = g_api.getInstanceForPackage(package_name.c_str());
  const ASensor* gyroscope = nullptr;
  const ASensor* accelerometer = nullptr;
  if (manager != nullptr) {
    gyroscope = g_api.getDefaultSensor(manager, kGyroscopeUncalibrated);
    if (gyroscope == nullptr) gyroscope = g_api.getDefaultSensor(manager, ASENSOR_TYPE_GYROSCOPE);
    accelerometer = g_api.getDefaultSensor(manager, ASENSOR_TYPE_ACCELEROMETER);
  }
  if (gyroscope == nullptr || accelerometer == nullptr) {
    FinishStart(-2);
    return;
  }

  ASensorEventQueue* queue = g_api.createEventQueue(manager, looper, kLooperId, nullptr, nullptr);
  const int32_t gyroscope_period = queue != nullptr ? Register(queue, gyroscope) : -1;
  const int32_t accelerometer_period = queue != nullptr ? Register(queue, accelerometer) : -1;
  if (gyroscope_period < 0 || accelerometer_period < 0) {
    if (queue != nullptr) {
      g_api.disableSensor(queue, gyroscope);
      g_api.disableSensor(queue, accelerometer);
      g_api.destroyEventQueue(manager, queue);
    }
    FinishStart(-3);
    return;
  }

  char description[256];
  snprintf(description, sizeof(description), "gyroscope \"%s\" every %d us, accelerometer \"%s\" every %d us",
           g_api.getName(gyroscope), gyroscope_period, g_api.getName(accelerometer), accelerometer_period);
  g_description = description;
  FinishStart(0);

  ASensorEvent events[64];
  while (g_running.load()) {
    if (g_api.pollOnce(kPollTimeoutMs, nullptr, nullptr, nullptr) != kLooperId) continue;
    ssize_t count;
    while ((count = g_api.getEvents(queue, events, 64)) > 0) {
      std::lock_guard<std::mutex> lock(g_buffer_mutex);
      for (ssize_t i = 0; i < count; i++) Push(events[i]);
    }
  }

  g_api.disableSensor(queue, gyroscope);
  g_api.disableSensor(queue, accelerometer);
  g_api.destroyEventQueue(manager, queue);
}

}  // namespace

extern "C" {

// Starts reading on a new thread. Returns 0 once both sensors deliver, or a negative error:
// -1 libandroid's sensor API is missing, -2 no gyroscope or accelerometer, -3 registration failed.
__attribute__((visibility("default"))) int CardboardXR_ImuStart(const char* package_name) {
  std::lock_guard<std::mutex> control(g_control_mutex);
  if (g_running.load()) return 0;
  if (!g_api.Load()) return -1;

  {
    std::lock_guard<std::mutex> lock(g_buffer_mutex);
    g_first = g_count = 0;
    g_dropped = 0;
  }
  {
    std::lock_guard<std::mutex> lock(g_start_mutex);
    g_start_done = false;
  }
  g_running = true;
  g_thread = std::thread(Run, std::string(package_name != nullptr ? package_name : ""));

  std::unique_lock<std::mutex> lock(g_start_mutex);
  g_start_changed.wait(lock, [] { return g_start_done; });
  const int status = g_start_status;
  lock.unlock();
  if (status != 0) {
    g_running = false;
    g_thread.join();
  }
  return status;
}

__attribute__((visibility("default"))) void CardboardXR_ImuStop() {
  std::lock_guard<std::mutex> control(g_control_mutex);
  if (!g_running.exchange(false)) return;
  g_thread.join();
}

// Moves up to max buffered samples, oldest first, into samples. Returns how many.
__attribute__((visibility("default"))) int CardboardXR_ImuRead(Sample* samples, int max) {
  std::lock_guard<std::mutex> lock(g_buffer_mutex);
  const int count = std::min(g_count, max);
  for (int i = 0; i < count; i++) samples[i] = g_buffer[(g_first + i) % kCapacity];
  g_first = (g_first + count) % kCapacity;
  g_count -= count;
  return count;
}

// The current time on the sample timestamps' clock.
__attribute__((visibility("default"))) int64_t CardboardXR_ImuNowNs() {
  timespec now;
  clock_gettime(CLOCK_BOOTTIME, &now);
  return static_cast<int64_t>(now.tv_sec) * 1000000000LL + now.tv_nsec;
}

// Samples discarded because the buffer was full since the last start.
__attribute__((visibility("default"))) int64_t CardboardXR_ImuDropped() {
  std::lock_guard<std::mutex> lock(g_buffer_mutex);
  return g_dropped;
}

// Writes the sensors and their periods as UTF-8 into buffer. Returns the length written.
__attribute__((visibility("default"))) int CardboardXR_ImuDescribe(char* buffer, int size) {
  std::lock_guard<std::mutex> control(g_control_mutex);
  if (buffer == nullptr || size <= 0) return 0;
  const int length = std::min(static_cast<int>(g_description.size()), size);
  g_description.copy(buffer, length);
  return length;
}

}  // extern "C"

#endif  // defined(__ANDROID__)
