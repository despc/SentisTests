from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "SentisTests" / "Core" / "TickMetrics.cs").read_text(encoding="utf-8-sig")


def test_frame_metrics_report_tail_latency_and_budget_overruns():
    for token in ("P95FrameMs", "P99FrameMs", "P999FrameMs", "Over50Ms", "Over100Ms", "Over250Ms"):
        assert token in SOURCE, token
    assert "Percentile(sorted, 0.999)" in SOURCE
    assert "> 100.0" in SOURCE
    assert "> 250.0" in SOURCE


if __name__ == "__main__":
    test_frame_metrics_report_tail_latency_and_budget_overruns()
    print("OK: TickMetrics tail-latency contract")
