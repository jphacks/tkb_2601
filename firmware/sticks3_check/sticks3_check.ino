// M5StickS3 動作確認用。IMU の振動 RMS・傾き・ボタンを JSON Lines で USB シリアルへ出す。
// 測定値は {"type":"sample",...} を送信間隔ごとに、ボタンなどは {"type":"event",...} をすぐに送る。
// KEY1（BtnA、正面）: 押して 1 秒待ち、手の揺れが収まってから 3 秒間測って基準値にする。
// KEY2（BtnB、右側面）: 押下イベントを出すだけ。
// 左側面のボタンは電源とリセット用なので、このプログラムでは使わない。
// 送信間隔は {"cmd":"interval","ms":500} を 1 行送ると変わる（100〜60000 ms）。
#include <M5Unified.h>

static constexpr const char* DEVICE_ID = "sticks3-01";
static constexpr uint32_t MIN_INTERVAL_MS = 100;
static constexpr uint32_t MAX_INTERVAL_MS = 60000;
static constexpr uint32_t BASELINE_SETTLE_MS = 1000;  // ボタンを押したときの揺れを測定に入れない
static constexpr uint32_t BASELINE_MS = 3000;
static constexpr float GRAVITY_ALPHA = 0.02f;  // 重力成分を追う移動平均。170 Hz で約 0.3 秒

static uint32_t intervalMs = 1000;
static uint32_t seq = 0;

static float meanX = 0, meanY = 0, meanZ = 1;
static bool meanReady = false;

// 送信間隔ごとの集計
static double sumSq = 0;
static float peak = 0;
static uint32_t samples = 0;
static uint32_t windowStart = 0;

// 基準値の測定
static bool calibrating = false;
static uint32_t calibrationStart = 0;
static double calSumSq = 0;
static uint32_t calSamples = 0;
static float baselineRms = 0;
static float baseX = 0, baseY = 0, baseZ = 1;
static bool hasBaseline = false;

static const char* lastButton = nullptr;
static uint32_t lastButtonAt = 0;

static String inputLine;

static float angleDeg(float ax, float ay, float az, float bx, float by, float bz)
{
  float na = sqrtf(ax * ax + ay * ay + az * az);
  float nb = sqrtf(bx * bx + by * by + bz * bz);
  if (na < 1e-6f || nb < 1e-6f) return 0;
  float c = (ax * bx + ay * by + az * bz) / (na * nb);
  c = c > 1 ? 1 : (c < -1 ? -1 : c);
  return acosf(c) * 57.29578f;
}

static void beginEvent(const char* name)
{
  Serial.printf("{\"type\":\"event\",\"dev\":\"%s\",\"seq\":%lu,\"t_ms\":%lu,\"event\":\"%s\"",
                DEVICE_ID, (unsigned long)seq++, (unsigned long)millis(), name);
}

static bool baselineMeasuring()
{
  return calibrating && millis() - calibrationStart >= BASELINE_SETTLE_MS;
}

static void sampleImu()
{
  if (!M5.Imu.update()) return;
  float x, y, z;
  M5.Imu.getAccel(&x, &y, &z);
  if (!meanReady) { meanX = x; meanY = y; meanZ = z; meanReady = true; }
  meanX += GRAVITY_ALPHA * (x - meanX);
  meanY += GRAVITY_ALPHA * (y - meanY);
  meanZ += GRAVITY_ALPHA * (z - meanZ);
  float dx = x - meanX, dy = y - meanY, dz = z - meanZ;
  float m2 = dx * dx + dy * dy + dz * dz;
  sumSq += m2;
  float m = sqrtf(m2);
  if (m > peak) peak = m;
  samples++;
  if (baselineMeasuring()) { calSumSq += m2; calSamples++; }
}

static void startBaseline()
{
  calibrating = true;
  calibrationStart = millis();
  calSumSq = 0;
  calSamples = 0;
  beginEvent("baseline_start");
  Serial.printf(",\"settle_ms\":%lu,\"duration_ms\":%lu}\n",
                (unsigned long)BASELINE_SETTLE_MS, (unsigned long)BASELINE_MS);
}

static void finishBaselineIfDone()
{
  if (!calibrating || millis() - calibrationStart < BASELINE_SETTLE_MS + BASELINE_MS) return;
  calibrating = false;
  baselineRms = calSamples ? sqrtf(calSumSq / calSamples) : 0;
  baseX = meanX; baseY = meanY; baseZ = meanZ;
  hasBaseline = baselineRms > 0;
  beginEvent("baseline");
  Serial.printf(",\"vib_rms_g\":%.4f,\"n\":%lu,\"gx\":%.3f,\"gy\":%.3f,\"gz\":%.3f}\n",
                baselineRms, (unsigned long)calSamples, baseX, baseY, baseZ);
}

static void reportButton(const char* name)
{
  lastButton = name;
  lastButtonAt = millis();
  beginEvent("button");
  Serial.printf(",\"btn\":\"%s\"}\n", name);
}

static void handleButtons()
{
  if (M5.BtnA.wasClicked())
  {
    reportButton("KEY1");
    startBaseline();
  }
  if (M5.BtnB.wasClicked()) reportButton("KEY2");
}

// {"cmd":"interval","ms":500} の形だけを受け付ける簡易パーサー
static void handleCommand(const String& line)
{
  if (line.indexOf("\"interval\"") < 0) return;
  int key = line.indexOf("\"ms\"");
  if (key < 0) return;
  int colon = line.indexOf(':', key);
  if (colon < 0) return;
  long ms = line.substring(colon + 1).toInt();
  if (ms < (long)MIN_INTERVAL_MS) ms = MIN_INTERVAL_MS;
  if (ms > (long)MAX_INTERVAL_MS) ms = MAX_INTERVAL_MS;
  intervalMs = (uint32_t)ms;
  beginEvent("interval");
  Serial.printf(",\"ms\":%lu}\n", (unsigned long)intervalMs);
}

static void readCommands()
{
  while (Serial.available() > 0)
  {
    char c = (char)Serial.read();
    if (c == '\n' || c == '\r')
    {
      if (inputLine.length() > 0) handleCommand(inputLine);
      inputLine = "";
    }
    else if (inputLine.length() < 128)
    {
      inputLine += c;
    }
  }
}

static void drawStatus(float rms, float pk, float ratio, float tilt, int bat)
{
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setCursor(0, 0);
  M5.Display.setTextColor(ratio >= 3 ? TFT_ORANGE : ratio >= 1.5f ? TFT_YELLOW : TFT_GREEN, TFT_BLACK);
  M5.Display.printf("vib  %6.1f mg\n", rms * 1000);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.printf("peak %6.1f mg\n", pk * 1000);
  if (calibrating) M5.Display.println(baselineMeasuring() ? "base measuring" : "base waiting");
  else if (hasBaseline) M5.Display.printf("ratio %5.2f x\n", ratio);
  else M5.Display.println("ratio (KEY1)");
  M5.Display.printf("tilt %6.1f deg\n", tilt);
  M5.Display.printf("%lums bat %d%%\n", (unsigned long)intervalMs, bat);
  if (lastButton && millis() - lastButtonAt < 3000)
  {
    M5.Display.setTextColor(TFT_CYAN, TFT_BLACK);
    M5.Display.printf("pressed %s\n", lastButton);
  }
}

static void report(uint32_t elapsed)
{
  float rms = samples ? sqrtf(sumSq / samples) : 0;
  float ratio = hasBaseline ? rms / baselineRms : -1;
  float tilt = hasBaseline ? angleDeg(meanX, meanY, meanZ, baseX, baseY, baseZ)
                           : angleDeg(meanX, meanY, meanZ, 0, 0, 1);
  int bat = M5.Power.getBatteryLevel();

  Serial.printf("{\"type\":\"sample\",\"dev\":\"%s\",\"seq\":%lu,\"t_ms\":%lu,\"win_ms\":%lu,\"n\":%lu,"
                "\"vib_rms_g\":%.4f,\"vib_peak_g\":%.4f,\"ratio\":%.2f,\"tilt_deg\":%.1f,"
                "\"gx\":%.3f,\"gy\":%.3f,\"gz\":%.3f,\"bat\":%d}\n",
                DEVICE_ID, (unsigned long)seq++, (unsigned long)millis(), (unsigned long)elapsed,
                (unsigned long)samples, rms, peak, ratio, tilt, meanX, meanY, meanZ, bat);
  drawStatus(rms, peak, ratio, tilt, bat);

  sumSq = 0; peak = 0; samples = 0;
}

void setup()
{
  auto cfg = M5.config();
  M5.begin(cfg);
  Serial.begin(115200);
  M5.Display.setRotation(1);
  M5.Display.setTextSize(2);
  delay(500);
  beginEvent("boot");
  Serial.printf(",\"is_sticks3\":%d,\"imu\":%d,\"interval_ms\":%lu}\n",
                M5.getBoard() == m5::board_t::board_M5StickS3, (int)M5.Imu.getType(), (unsigned long)intervalMs);
  windowStart = millis();
}

void loop()
{
  M5.update();
  sampleImu();
  handleButtons();
  readCommands();
  finishBaselineIfDone();
  uint32_t now = millis();
  if (now - windowStart >= intervalMs)
  {
    report(now - windowStart);
    windowStart = now;
  }
}
