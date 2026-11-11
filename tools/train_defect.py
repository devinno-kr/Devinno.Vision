"""불량 분류 모델 학습 — Studio 의 ONNX 검사 [학습] 이 부른다.

python train_defect.py --data DIR --out DIR [--size 224] [--epochs 15] [--weights resnet18-f37072fd.pth] [--good good]

DIR/<라벨>/*.png : Studio 가 정렬하고 검사 ROI 로 잘라 둔 이미지 (good 폴더 = 양품)
OUT/model.onnx   : 입력 'image' [N,3,S,S] (RGB, ImageNet 정규화) → 출력 'scores' [N,라벨 수]
OUT/model.json   : Labels · GoodLabel · InputWidth · InputHeight · Normalize · Accuracy · Images
진행 줄: 'PROGRESS <에폭> <에폭 수> <손실> <검증 정확도>', 끝: 'DONE <검증 정확도>'

모델: ImageNet ResNet18 의 layer1~3 (가중치 파일이 있으면 불러오고, 없으면 내려받기를 시도, 그것도 안 되면 처음부터) + 분류 머리.
앞부분은 고정하고 layer3 · 머리만 학습한다 (불량 이미지가 적어도 되게). 필요: torch, numpy, pillow, onnx
"""
import argparse, json, os, random, sys, time

import numpy as np
import torch
from PIL import Image
from torch import nn

MEAN = np.array([0.485, 0.456, 0.406], np.float32)
STD = np.array([0.229, 0.224, 0.225], np.float32)
EXT = (".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff")


class Block(nn.Module):
    def __init__(self, cin, cout, stride):
        super().__init__()
        self.conv1 = nn.Conv2d(cin, cout, 3, stride, 1, bias=False); self.bn1 = nn.BatchNorm2d(cout)
        self.conv2 = nn.Conv2d(cout, cout, 3, 1, 1, bias=False); self.bn2 = nn.BatchNorm2d(cout)
        self.relu = nn.ReLU(inplace=True)
        self.downsample = None
        if stride != 1 or cin != cout:
            self.downsample = nn.Sequential(nn.Conv2d(cin, cout, 1, stride, bias=False), nn.BatchNorm2d(cout))

    def forward(self, x):
        idt = x if self.downsample is None else self.downsample(x)
        y = self.relu(self.bn1(self.conv1(x)))
        return self.relu(self.bn2(self.conv2(y)) + idt)


class DefectNet(nn.Module):
    def __init__(self, classes):
        super().__init__()
        self.conv1 = nn.Conv2d(3, 64, 7, 2, 3, bias=False); self.bn1 = nn.BatchNorm2d(64)
        self.relu = nn.ReLU(inplace=True); self.maxpool = nn.MaxPool2d(3, 2, 1)
        self.layer1 = nn.Sequential(Block(64, 64, 1), Block(64, 64, 1))
        self.layer2 = nn.Sequential(Block(64, 128, 2), Block(128, 128, 1))
        self.layer3 = nn.Sequential(Block(128, 256, 2), Block(256, 256, 1))
        self.head = nn.Linear(256, classes)

    def forward(self, x):
        x = self.maxpool(self.relu(self.bn1(self.conv1(x))))
        x = self.layer3(self.layer2(self.layer1(x)))
        return self.head(x.mean((2, 3)))


def load(path, size):
    im = Image.open(path).convert("RGB").resize((size, size), Image.BILINEAR)
    return np.asarray(im, np.float32)


def to_tensor(im):
    return ((im / 255 - MEAN) / STD).transpose(2, 0, 1)


def augment(im):
    s = im.shape[0]
    pad = max(1, s // 25)                                    # 정렬 오차만큼 조금 흔든다
    big = np.pad(im, ((pad, pad), (pad, pad), (0, 0)), mode="edge")
    y, x = random.randint(0, 2 * pad), random.randint(0, 2 * pad)
    im = big[y:y + s, x:x + s]
    return np.clip(im * random.uniform(0.9, 1.1) + random.uniform(-10, 10), 0, 255)   # 조명 차이


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--size", type=int, default=224)
    ap.add_argument("--epochs", type=int, default=15)
    ap.add_argument("--weights")
    ap.add_argument("--good", default="good")
    a = ap.parse_args()
    random.seed(0); np.random.seed(0); torch.manual_seed(0)

    labels = sorted(d for d in os.listdir(a.data) if os.path.isdir(os.path.join(a.data, d)))
    files = {l: sorted(os.path.join(a.data, l, f) for f in os.listdir(os.path.join(a.data, l)) if f.lower().endswith(EXT)) for l in labels}
    labels = [l for l in labels if files[l]]
    if a.good not in labels or len(labels) < 2:
        print(f"ERROR 양품 폴더 '{a.good}' 와 불량 폴더 1개 이상이 필요합니다 (있는 것: {labels})", file=sys.stderr)
        sys.exit(2)

    # 라벨마다 20 % 를 검증용으로 (2장 이상일 때)
    train, val = [], []
    for k, l in enumerate(labels):
        fs = files[l][:]
        random.shuffle(fs)
        n = len(fs) // 5 if len(fs) >= 5 else (1 if len(fs) >= 2 else 0)
        val += [(f, k) for f in fs[:n]]
        train += [(f, k) for f in fs[n:]]
    imgs = {f: load(f, a.size) for f, _ in train + val}

    net = DefectNet(len(labels))
    state = None
    if a.weights and os.path.exists(a.weights):
        state = torch.load(a.weights, map_location="cpu")
    else:
        try:
            state = torch.hub.load_state_dict_from_url("https://download.pytorch.org/models/resnet18-f37072fd.pth", progress=False, map_location="cpu")
        except Exception as ex:                                # 내려받기 실패 → 처음부터 (정확도가 낮을 수 있다)
            print(f"WARN 사전 학습 가중치를 받지 못해 처음부터 학습합니다: {ex}", flush=True)
    if state is not None:
        net.load_state_dict({k: v for k, v in state.items() if not k.startswith(("fc.", "layer4."))}, strict=False)
    for name, p in net.named_parameters():
        p.requires_grad = name.startswith(("layer3", "head"))
    frozen = [net.conv1, net.bn1, net.layer1, net.layer2]

    counts = np.bincount([k for _, k in train], minlength=len(labels)).astype(np.float32)
    weight = torch.tensor(counts.sum() / np.maximum(counts, 1) / len(labels))   # 적은 라벨에 무게
    loss_fn = nn.CrossEntropyLoss(weight=weight)
    opt = torch.optim.Adam([p for p in net.parameters() if p.requires_grad], lr=1e-3)

    def accuracy(items):
        if not items:
            return float("nan")
        net.eval()
        with torch.no_grad():
            x = torch.from_numpy(np.stack([to_tensor(imgs[f]) for f, _ in items]))
            pred = net(x).argmax(1).numpy()
        return float((pred == np.array([k for _, k in items])).mean())

    t0 = time.time()
    acc = float("nan")
    for e in range(1, a.epochs + 1):
        random.shuffle(train)
        net.train()
        for m in frozen:
            m.eval()
        total = 0.0
        for i in range(0, len(train), 16):
            batch = train[i:i + 16]
            x = torch.from_numpy(np.stack([to_tensor(augment(imgs[f])) for f, _ in batch]).astype(np.float32))
            y = torch.tensor([k for _, k in batch])
            loss = loss_fn(net(x), y)
            opt.zero_grad(); loss.backward(); opt.step()
            total += loss.item() * len(batch)
        acc = accuracy(val if val else train)
        print(f"PROGRESS {e} {a.epochs} {total / len(train):.4f} {acc:.3f} ({time.time() - t0:.0f}s)", flush=True)

    net.eval()
    os.makedirs(a.out, exist_ok=True)
    torch.onnx.export(net, torch.zeros(1, 3, a.size, a.size), os.path.join(a.out, "model.onnx"), input_names=["image"], output_names=["scores"],
                      dynamic_axes={"image": {0: "n"}, "scores": {0: "n"}}, opset_version=17, dynamo=False)
    info = {"Labels": labels, "GoodLabel": a.good, "InputWidth": a.size, "InputHeight": a.size, "Normalize": "ImageNet",
            "Accuracy": None if acc != acc else round(acc, 4), "Validation": len(val), "Images": {l: len(files[l]) for l in labels}}
    with open(os.path.join(a.out, "model.json"), "w", encoding="utf-8") as f:
        json.dump(info, f, ensure_ascii=False, indent=2)
    print(f"DONE {acc:.3f}", flush=True)


if __name__ == "__main__":
    main()
