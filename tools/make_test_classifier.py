"""테스트용 아주 작은 불량 분류 모델 (무작위 고정 가중치) — tests/Devinno.Vision.Tests/Models/tiny_classifier.onnx
입력 'image' [1,3,32,32] → 'scores' [1,3] (good · scratch · dent 의 점수)
"""
import sys, torch
from torch import nn

torch.manual_seed(0)


class Tiny(nn.Module):
    def __init__(self):
        super().__init__()
        self.c = nn.Conv2d(3, 4, 3, 2, 1)
        self.fc = nn.Linear(4, 3)

    def forward(self, x):
        return self.fc(torch.relu(self.c(x)).mean((2, 3)))


out = sys.argv[1] if len(sys.argv) > 1 else "tiny_classifier.onnx"
torch.onnx.export(Tiny().eval(), torch.zeros(1, 3, 32, 32), out, input_names=["image"], output_names=["scores"], opset_version=17, dynamo=False)
print(out)
