"""センサー中継の判定のテスト。実行: python -m unittest scripts/sensor_bridge/test_sensor_bridge.py"""
import unittest
from unittest import mock

from sensor_bridge import Judge, JudgeConfig


def started_judge(now=0.0):
    """データを 1 件受け取った状態（Normal、保持なし）。"""
    judge = Judge(JudgeConfig())
    judge.on_sample(2.0, 1000, now)
    return judge


class JudgeTest(unittest.TestCase):
    def test_starts_normal_and_waits_for_data_without_holding(self):
        judge = Judge(JudgeConfig())
        self.assertFalse(judge.has_data)
        self.assertIsNone(judge.tick(60.0))  # データがまだ来ていないだけでは保持にしない
        self.assertFalse(judge.hold)
        judge.on_sample(2.0, 1000, 61.0)
        self.assertTrue(judge.has_data)
        state = judge.robot_state("B", 61.0)
        self.assertEqual(state["final"], "Normal")
        self.assertEqual(state["trust"], "Trusted")

    def test_key2_refused_without_data(self):
        judge = Judge(JudgeConfig())
        accepted, _ = judge.on_key2(0.0)
        self.assertFalse(accepted)

    def test_one_reading_over_threshold_does_not_raise(self):
        judge = started_judge()
        self.assertIsNone(judge.on_sample(500.0, 1000, 1.0))
        self.assertIsNone(judge.on_sample(2.0, 1000, 2.0))
        self.assertIsNone(judge.on_sample(500.0, 1000, 3.0))
        self.assertEqual(judge.level, "Normal")

    def test_two_readings_raise_to_warning(self):
        judge = started_judge()
        judge.on_sample(320.0, 1000, 1.0)
        reason = judge.on_sample(650.0, 1000, 2.0)
        self.assertEqual(judge.level, "Warning")
        self.assertIn("2 回続いたため注意", reason)
        self.assertEqual(judge.robot_state("B", 2.0)["final"], "Warning")

    def test_threshold_is_inclusive_and_light_shaking_stays_normal(self):
        judge = started_judge()
        for t in range(1, 10):
            judge.on_sample(299.9, 1000, float(t))
        self.assertEqual(judge.level, "Normal")
        judge.on_sample(300.0, 1000, 10.0)
        judge.on_sample(300.0, 1000, 11.0)
        self.assertEqual(judge.level, "Warning")

    def test_six_readings_raise_to_limited(self):
        judge = started_judge()
        levels = [judge.on_sample(700.0, 1000, float(t)) and judge.level for t in range(1, 7)]
        self.assertEqual(levels, [None, "Warning", None, None, None, "Limited"])
        self.assertIn("6 回続いたため制限", judge.reason)

    def test_a_quiet_second_restarts_the_count(self):
        judge = started_judge()
        for t in range(1, 6):
            judge.on_sample(700.0, 1000, float(t))
        self.assertEqual(judge.level, "Warning")
        judge.on_sample(50.0, 1000, 6.0)
        for t in range(7, 12):
            judge.on_sample(700.0, 1000, float(t))
        self.assertEqual(judge.level, "Warning")
        judge.on_sample(700.0, 1000, 12.0)
        self.assertEqual(judge.level, "Limited")

    def test_level_does_not_drop_by_itself(self):
        judge = started_judge()
        for t in range(1, 7):
            judge.on_sample(700.0, 1000, float(t))
        for t in range(7, 20):
            judge.on_sample(2.0, 1000, float(t))
        self.assertEqual(judge.level, "Limited")

    def test_key2_returns_to_normal_and_rises_again_if_still_shaking(self):
        judge = started_judge()
        for t in range(1, 7):
            judge.on_sample(700.0, 1000, float(t))
        self.assertTrue(judge.on_key2(6.5)[0])
        self.assertEqual(judge.level, "Normal")
        self.assertIsNone(judge.on_sample(700.0, 1000, 7.0))
        judge.on_sample(700.0, 1000, 8.0)
        self.assertEqual(judge.level, "Warning")

    def test_silence_holds_and_data_alone_does_not_release(self):
        judge = started_judge(now=0.0)
        # 1 秒間隔なら max(2.5 秒, 3 × 1 秒) = 3 秒で保持
        self.assertIsNone(judge.tick(2.9))
        reason = judge.tick(3.1)
        self.assertIn("途切れた", reason)
        self.assertEqual(judge.robot_state("B", 3.1)["final"], "Hold")
        self.assertEqual(judge.robot_state("B", 3.1)["trust"], "Untrusted")
        judge.on_sample(2.0, 1000, 5.0)
        self.assertEqual(judge.robot_state("B", 5.0)["final"], "Hold")
        self.assertEqual(judge.robot_state("B", 5.0)["trust"], "Trusted")
        self.assertTrue(judge.on_key2(5.0)[0])
        self.assertEqual(judge.robot_state("B", 5.0)["final"], "Normal")

    def test_silence_limit_is_at_least_two_and_a_half_seconds(self):
        judge = Judge(JudgeConfig())
        judge.on_sample(2.0, 500, 0.0)
        judge.on_key2(0.0)
        self.assertEqual(judge.stale_after(), 2.5)
        self.assertIsNone(judge.tick(2.4))
        self.assertIsNotNone(judge.tick(2.6))

    def test_silence_limit_follows_slow_send_interval(self):
        judge = Judge(JudgeConfig())
        judge.on_sample(2.0, 2000, 0.0)
        judge.on_key2(0.0)
        self.assertEqual(judge.stale_after(), 6.0)
        self.assertIsNone(judge.tick(5.9))
        self.assertIsNotNone(judge.tick(6.1))

    def test_level_rises_while_held_but_final_stays_hold(self):
        judge = started_judge(now=0.0)
        self.assertIsNotNone(judge.tick(10.0))  # 途切れて保持
        for t in range(11, 17):
            judge.on_sample(700.0, 1000, float(t))
        state = judge.robot_state("B", 16.0)
        self.assertEqual(state["capability"], "Limited")
        self.assertEqual(state["final"], "Hold")

    def test_packet_fields_match_unity_bridge(self):
        state = started_judge().robot_state("C", 0.5)
        self.assertEqual(set(state), {"id", "final", "capability", "trust", "measured", "reasons"})
        self.assertEqual(state["id"], "C")
        self.assertIs(state["measured"], True)
        self.assertIsInstance(state["reasons"], list)

    def test_note_alive_keeps_data_fresh_without_judging(self):
        judge = Judge(JudgeConfig())
        judge.note_alive(1663, 0.0)
        judge.note_alive(1000, 1.0)
        self.assertEqual(judge.level, "Normal")
        self.assertTrue(judge.fresh(1.0))
        self.assertTrue(judge.on_key2(1.0)[0])

    def test_invalid_thresholds_rejected(self):
        with self.assertRaises(ValueError):
            Judge(JudgeConfig(warning_count=6, limited_count=2))
        with self.assertRaises(ValueError):
            Judge(JudgeConfig(threshold_mg=0))

    def test_counts_can_be_changed(self):
        judge = Judge(JudgeConfig(threshold_mg=500, warning_count=1, limited_count=3))
        judge.on_sample(2.0, 1000, 0.0)
        judge.on_key2(0.0)
        judge.on_sample(499.0, 1000, 1.0)
        self.assertEqual(judge.level, "Normal")
        judge.on_sample(500.0, 1000, 2.0)
        self.assertEqual(judge.level, "Warning")
        judge.on_sample(500.0, 1000, 3.0)
        judge.on_sample(500.0, 1000, 4.0)
        self.assertEqual(judge.level, "Limited")


class UnityPortTest(unittest.TestCase):
    """47610 を、ほかのプログラムが押さえている PC がある。番号を指定しなければ 18610 で受ける。"""

    def test_falls_back_when_47610_is_taken_and_keeps_an_explicit_port(self):
        import queue as _queue
        import socket as _socket
        import threading as _threading
        import sensor_bridge as bridge
        events, stop = _queue.Queue(), _threading.Event()
        holder = _socket.create_server(("127.0.0.1", 0))
        taken = holder.getsockname()[1]
        try:
            with mock.patch.object(bridge, "UNITY_PORT", taken), mock.patch.object(bridge, "FALLBACK_PORT", 0):
                server, port = bridge.open_unity_server(None, events, stop)   # 0 は「空いている番号」
                self.assertNotEqual(port, taken)
                server.close()
                with self.assertRaises(OSError):
                    bridge.open_unity_server(taken, events, stop)          # 指定された番号は、ずらさない
        finally:
            holder.close()


if __name__ == "__main__":
    unittest.main()
