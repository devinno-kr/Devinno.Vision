"""AnomalyModel 용 특징 모델 만들기: ImageNet ResNet18 의 layer2 · layer3 출력만 내는 ONNX.

필요: torch (torchvision 은 필요 없음 — 구조를 직접 정의), onnx, onnxruntime
실행: python tools/export_features.py [출력 경로]   (기본: resnet18_f23.onnx)
입력 'image' [N,3,H,W] (RGB, ImageNet 정규화 = VisionTensorFormat.ImageNet) → 'f2' [N,128,H/8,W/8], 'f3' [N,256,H/16,W/16]
가중치: https://download.pytorch.org/models/resnet18-f37072fd.pth (약 45 MB, 처음 한 번 받음)
"""
import os, sys
import numpy as np
import torch
from torch import nn


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


class ResNet18Features(nn.Module):
    def __init__(self):
        super().__init__()
        self.conv1 = nn.Conv2d(3, 64, 7, 2, 3, bias=False); self.bn1 = nn.BatchNorm2d(64)
        self.relu = nn.ReLU(inplace=True); self.maxpool = nn.MaxPool2d(3, 2, 1)
        self.layer1 = nn.Sequential(Block(64, 64, 1), Block(64, 64, 1))
        self.layer2 = nn.Sequential(Block(64, 128, 2), Block(128, 128, 1))
        self.layer3 = nn.Sequential(Block(128, 256, 2), Block(256, 256, 1))

    def forward(self, x):
        x = self.maxpool(self.relu(self.bn1(self.conv1(x))))
        f2 = self.layer2(self.layer1(x))
        return f2, self.layer3(f2)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "resnet18_f23.onnx"
    sd = torch.hub.load_state_dict_from_url("https://download.pytorch.org/models/resnet18-f37072fd.pth", progress=False)
    m = ResNet18Features()
    missing, _ = m.load_state_dict(sd, strict=False)
    assert not missing, missing
    m.eval()
    torch.onnx.export(m, torch.zeros(1, 3, 256, 256), out, input_names=["image"], output_names=["f2", "f3"],
                      dynamic_axes={"image": {0: "n", 2: "h", 3: "w"}, "f2": {0: "n", 2: "h2", 3: "w2"}, "f3": {0: "n", 2: "h3", 3: "w3"}},
                      opset_version=17, dynamo=False)
    import onnxruntime as ort
    x = np.random.rand(1, 3, 256, 256).astype(np.float32)
    with torch.no_grad():
        r2, r3 = m(torch.from_numpy(x))
    g2, g3 = ort.InferenceSession(out, providers=["CPUExecutionProvider"]).run(None, {"image": x})
    print(f"{out}: f2 {g2.shape} f3 {g3.shape}, max diff vs torch {max(abs(r2.numpy() - g2).max(), abs(r3.numpy() - g3).max()):.1e}, "
          f"{os.path.getsize(out) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
