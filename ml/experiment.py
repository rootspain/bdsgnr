"""Which features carry the bench and which only the generator's handwriting?

Trains the same model on several feature sets and reports, for each: accuracy on the validation scenes, on the shifted
distribution and on the traps, and — on a real drawing's features (no true classes) — the share of length the model
puts in each class and how much of it agrees with the rules, which gave a plausible picture there.

    ml/.venv/Scripts/python ml/experiment.py [samples/real1.features.csv]
"""
import sys
from pathlib import Path

import lightgbm as lgb
import numpy as np
import pandas as pd

from train import CLASSES, HANDWRITING, feature_names, load, scores, smooth

VARIANTS = {
    "all": [],
    "no handwriting": HANDWRITING,
    "no handwriting, no closed/roughness": HANDWRITING + ["line_closed", "seg_roughness"],
}


def fit(tr: pd.DataFrame, va: pd.DataFrame, names: list[str]) -> lgb.LGBMClassifier:
    by = tr.groupby("y")["seg_length"].sum()
    cw = (by.sum() / (len(by) * by)).to_dict()
    m = lgb.LGBMClassifier(objective="multiclass", n_estimators=3000, learning_rate=0.05, num_leaves=31, min_child_samples=40,
                           subsample=0.8, subsample_freq=1, colsample_bytree=0.8, reg_lambda=1.0, random_state=0, verbose=-1)
    m.fit(tr[names].values.astype(np.float32), tr["y"].values, sample_weight=tr["seg_length"].values * tr["y"].map(cw).values,
          eval_set=[(va[names].values.astype(np.float32), va["y"].values)],
          eval_sample_weight=[va["seg_length"].values * va["y"].map(cw).values], callbacks=[lgb.early_stopping(100, verbose=False)])
    return m


def main() -> None:
    data = Path("ml/data")
    train = load(data / "train.csv")
    shifted, traps = load(data / "shifted.csv"), load(data / "traps.csv")
    real = pd.read_csv(sys.argv[1]) if len(sys.argv) > 1 else None
    if real is not None:
        real["rule_y"] = real["rule"].map(CLASSES.index)
    va_mask = (train["scene"] % 7) == 0
    tr, va = train[~va_mask].reset_index(drop=True), train[va_mask].reset_index(drop=True)
    for name, drop in VARIANTS.items():
        names = [n for n in feature_names(train) if n not in drop]
        m = fit(tr, va, names)
        line = f"{name:38} trees {m.best_iteration_:4}"
        for label, df in [("val", va), ("shifted", shifted), ("traps", traps)]:
            pred, _ = smooth(df, m.predict_proba(df[names].values.astype(np.float32)))
            line += f"   {label} {scores(df, pred)['accuracy']:.1%}"
        print(line)
        if real is not None:
            pred, conf = smooth(real, m.predict_proba(real[names].values.astype(np.float32)))
            w = real["seg_length"].values
            agree = (w * (pred == real["rule_y"].values)).sum() / w.sum()
            share = "  ".join(f"{c} {w[pred == k].sum() / w.sum():.0%}" for k, c in enumerate(CLASSES))
            rules = "  ".join(f"{c} {w[real['rule_y'].values == k].sum() / w.sum():.0%}" for k, c in enumerate(CLASSES))
            print(f"{'':38} real: agrees with rules {agree:.0%}; model {share}")
            print(f"{'':38}       rules {rules}; unsure {w[conf < 0.5].sum() / w.sum():.0%}")
        gain = m.booster_.feature_importance(importance_type="gain")
        print(f"{'':38} top: " + ", ".join(f"{n} {g / gain.sum():.0%}" for n, g in sorted(zip(names, gain), key=lambda x: -x[1])[:6]))


if __name__ == "__main__":
    main()
