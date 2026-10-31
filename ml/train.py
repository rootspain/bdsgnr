"""Survey segment classifier: LightGBM on the segment features, exported to ONNX.

Reads the synthetic datasets made by tools/BlastDesigner.SurveyData (ml/data/*.csv), trains on train.csv with a
validation split by scene, and compares the model with the rules on the same segments (the "rule" column) on the
validation scenes, on shifted.csv (a different distribution, never trained on) and on traps.csv (hard cases).
Model predictions get the same smoothing along the line as the rules (SurveyRecognizer.Smooth).

    ml/.venv/Scripts/python ml/train.py            # writes ml/models/survey-segments.onnx, metrics.json, parity.json
"""
import argparse
import json
from pathlib import Path

import lightgbm as lgb
import numpy as np
import onnxruntime as ort
import pandas as pd
from onnxmltools import convert_lightgbm
from onnxmltools.convert.common.data_types import FloatTensorType

# Order of SurveyLineClass in BlastDesigner.Engine; the model's output columns follow it.
CLASSES = ["Crest", "Toe", "Contour", "Boundary", "Other"]
META = ["scene", "trap", "line", "from", "to", "label", "rule"]
FEATURE_VERSION = 1  # SurveyFeatures.Version
MODEL_VERSION = 1
CHECK_BELOW = 0.5  # SurveyRecognizer.CheckBelow
# Features that describe the generator's handwriting (vertex spacing, line length, how many lines share a layer)
# more than the bench: without them the model holds up better on the shifted data and agrees more with the rules on the
# real drawing (ml/experiment.py). The ONNX model lists the features it takes; the program passes those, by name.
HANDWRITING = ["line_spacing_med", "line_spacing_cv", "line_length", "line_sinuosity", "seg_rel_pos", "pts_on_vertices", "layer_share"]


def load(path: Path) -> pd.DataFrame:
    df = pd.read_csv(path)
    df["y"] = df["label"].map(CLASSES.index)
    df["rule_y"] = df["rule"].map(CLASSES.index)
    return df


def feature_names(df: pd.DataFrame) -> list[str]:
    return [c for c in df.columns if c not in META + ["y", "rule_y"]]


def smooth(df: pd.DataFrame, proba: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Class and confidence per segment, then the 5-segment confidence-weighted vote along each line (as the rules)."""
    order = np.argsort(-proba, axis=1)
    best = proba[np.arange(len(proba)), order[:, 0]]
    second = proba[np.arange(len(proba)), order[:, 1]]
    cls = order[:, 0].copy()
    conf = np.where(best > 0, (best - second) / np.maximum(best, 1e-12), 0)
    out_cls, out_conf = cls.copy(), conf.copy()
    keys = df["scene"].astype(str) + "/" + df["line"].astype(str)
    for _, idx in df.groupby(keys, sort=False).indices.items():
        idx = idx[np.argsort(df["from"].values[idx])]
        for k in range(len(idx)):
            window = idx[max(0, k - 2):min(len(idx), k + 3)]
            weights = {}
            for j in window:
                weights[cls[j]] = weights.get(cls[j], 0) + conf[j]
            vote, weight = max(weights.items(), key=lambda kv: kv[1])
            total = sum(weights.values())
            own = idx[k]
            if vote != cls[own] and weight >= total * 0.6:
                out_cls[own] = vote
                out_conf[own] = min(conf[own], weight / max(total, 1e-12))
    return out_cls, out_conf


def scores(df: pd.DataFrame, pred: np.ndarray) -> dict:
    y, w = df["y"].values, df["seg_length"].values
    result = {"accuracy": float((w * (pred == y)).sum() / w.sum())}
    for k, c in enumerate(CLASSES):
        true_k, pred_k = y == k, pred == k
        hit = (w * (true_k & pred_k)).sum()
        result[c] = {
            "recall": float(hit / max(w[true_k].sum(), 1e-9)),
            "precision": float(hit / max(w[pred_k].sum(), 1e-9)),
        }
    return result


def report(name: str, df: pd.DataFrame, pred: np.ndarray, conf: np.ndarray) -> dict:
    model, rules = scores(df, pred), scores(df, df["rule_y"].values)
    w, y = df["seg_length"].values, df["y"].values
    unsure = conf < CHECK_BELOW
    check = {
        "share": float(w[unsure].sum() / w.sum()),
        "wrong_among_marked": float((w * (unsure & (pred != y))).sum() / max(w[unsure].sum(), 1e-9)),
        "wrong_among_rest": float((w * (~unsure & (pred != y))).sum() / max(w[~unsure].sum(), 1e-9)),
    }
    print(f"\n== {name}: {df['scene'].nunique()} scenes, {len(df)} segments")
    print(f"   accuracy by length   model {model['accuracy']:.1%}   rules {rules['accuracy']:.1%}")
    for c in CLASSES:
        if (y == CLASSES.index(c)).any():
            m, r = model[c], rules[c]
            print(f"   {c:9} recall {m['recall']:6.1%} (rules {r['recall']:6.1%})   precision {m['precision']:6.1%} (rules {r['precision']:6.1%})")
    print(f"   marked to check {check['share']:.1%} of length, wrong among them {check['wrong_among_marked']:.1%}, among the rest {check['wrong_among_rest']:.1%}")
    traps = {}
    if df["trap"].nunique() > 1:
        for trap, part in df.groupby("trap"):
            idx = part.index.values
            traps[trap] = {"model": scores(part, pred[idx])["accuracy"], "rules": scores(part, part["rule_y"].values)["accuracy"]}
            print(f"   {trap:18} model {traps[trap]['model']:.1%}   rules {traps[trap]['rules']:.1%}")
    return {"model": model, "rules": rules, "check": check, "traps": traps}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="ml/data")
    ap.add_argument("--out", default="ml/models")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()
    data, out = Path(args.data), Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    train = load(data / "train.csv")
    names = [n for n in feature_names(train) if n not in HANDWRITING]
    # validation: every seventh scene, so no scene is on both sides
    val_mask = (train["scene"] % 7) == 0
    tr, va = train[~val_mask].reset_index(drop=True), train[val_mask].reset_index(drop=True)

    # every class weighs the same in total length; the long, plentiful contours do not drown the toes
    length_by_class = tr.groupby("y")["seg_length"].sum()
    class_weight = (length_by_class.sum() / (len(length_by_class) * length_by_class)).to_dict()
    w_tr = tr["seg_length"].values * tr["y"].map(class_weight).values
    w_va = va["seg_length"].values * va["y"].map(class_weight).values

    model = lgb.LGBMClassifier(
        objective="multiclass", n_estimators=3000, learning_rate=0.05, num_leaves=31, min_child_samples=40,
        subsample=0.8, subsample_freq=1, colsample_bytree=0.8, reg_lambda=1.0, random_state=args.seed, verbose=-1,
    )
    model.fit(tr[names].values.astype(np.float32), tr["y"].values, sample_weight=w_tr,
              eval_set=[(va[names].values.astype(np.float32), va["y"].values)], eval_sample_weight=[w_va],
              callbacks=[lgb.early_stopping(100, verbose=False)])
    print(f"trees: {model.best_iteration_} per class, {len(names)} features")

    metrics = {"model_version": MODEL_VERSION, "feature_version": FEATURE_VERSION, "trees": int(model.best_iteration_)}
    sets = {"validation": va}
    for extra in ["shifted", "traps"]:
        if (data / f"{extra}.csv").exists():
            sets[extra] = load(data / f"{extra}.csv")
    for name, df in sets.items():
        proba = model.predict_proba(df[names].values.astype(np.float32))
        pred, conf = smooth(df, proba)
        metrics[name] = report(name, df, pred, conf)

    gain = model.booster_.feature_importance(importance_type="gain")
    top = sorted(zip(names, gain), key=lambda x: -x[1])[:15]
    print("\nmost useful features: " + ", ".join(f"{n} ({g / gain.sum():.0%})" for n, g in top))
    metrics["importance"] = {n: float(g / gain.sum()) for n, g in sorted(zip(names, gain), key=lambda x: -x[1])}

    # ONNX: one input "features" [n, F] float, outputs label and probabilities [n, classes] (no zipmap)
    onx = convert_lightgbm(model, initial_types=[("features", FloatTensorType([None, len(names)]))], zipmap=False, target_opset=15)
    for key, value in {"model_version": str(MODEL_VERSION), "feature_version": str(FEATURE_VERSION), "features": ",".join(names),
                       "classes": ",".join(CLASSES), "check_below": str(CHECK_BELOW)}.items():
        prop = onx.metadata_props.add()
        prop.key, prop.value = key, value
    path = out / "survey-segments.onnx"
    path.write_bytes(onx.SerializeToString())

    # the ONNX model must give the same probabilities as LightGBM
    session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    sample = va.sample(n=min(2000, len(va)), random_state=1)
    x = sample[names].values.astype(np.float32)
    onnx_proba = session.run(None, {"features": x})[1]
    diff = float(np.abs(onnx_proba - model.predict_proba(x)).max())
    print(f"\n{path}: {path.stat().st_size / 1024:.0f} KB, largest difference ONNX vs LightGBM {diff:.2e}")
    if diff > 1e-4:
        raise SystemExit("ONNX export differs from the trained model")

    # reference rows for the C# test: features in, probabilities out
    parity = sample.head(60)
    (out / "parity.json").write_text(json.dumps({
        "features": names, "classes": CLASSES,
        "rows": [{"x": [float(v) for v in r], "p": [float(v) for v in p]} for r, p in zip(parity[names].values.astype(np.float32), onnx_proba[:60])],
    }, indent=1))
    (out / "metrics.json").write_text(json.dumps(metrics, indent=1))


if __name__ == "__main__":
    main()
