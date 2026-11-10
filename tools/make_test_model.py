"""테스트용 아주 작은 특징 모델 (무작위 고정 가중치) — tests/Devinno.Vision.Tests/Models/tiny_features.onnx
입력 'image' [1,3,64,64] → 'a' [1,8,16,16], 'b' [1,16,8,8]
"""
import sys, torch
from torch import nn

torch.manual_seed(0)


class Tiny(nn.Module):
    def __init__(self):
        super().__init__()
        self.c1 = nn.Conv2d(3, 8, 3, 4, 1)
        self.c2 = nn.Conv2d(8, 16, 3, 2, 1)

    def forward(self, x):
        a = torch.relu(self.c1(x))
        return a, torch.relu(self.c2(a))


out = sys.argv[1] if len(sys.argv) > 1 else "tiny_features.onnx"
torch.onnx.export(Tiny().eval(), torch.zeros(1, 3, 64, 64), out, input_names=["image"], output_names=["a", "b"], opset_version=17, dynamo=False)
print(out)
