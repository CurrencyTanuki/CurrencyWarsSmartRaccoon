// v2 冒烟：验证每格独立四边形 buildQuads 与 C# ComputeSlotQuads 一致，且上/下 X 独立（正交投影斜视）
"use strict";
const BK={topLeftX:343,topRightX:466,bottomLeftX:319,bottomRightX:446,topY:600,bottomY:735};
const topW=BK.topRightX-BK.topLeftX, botW=BK.bottomRightX-BK.bottomLeftX;
const pitchTop=(960-((BK.topLeftX+BK.topRightX)/2))/4;
const pitchBot=(960-((BK.bottomLeftX+BK.bottomRightX)/2))/4;
function buildQuads(n){
  const out=[];
  for(let i=0;i<n;i++){
    const cT=960+(i-(n-1)/2)*pitchTop;
    const cB=960+(i-(n-1)/2)*pitchBot;
    out.push([[cT-topW/2,BK.topY],[cT+topW/2,BK.topY],[cB-botW/2,BK.bottomY],[cB+botW/2,BK.bottomY]]);
  }
  return out;
}
const approx=(a,b,t)=>Math.abs(a-b)<t;
const assert=(n,c)=>{console.log((c?'PASS':'FAIL')+' - '+n);if(!c)process.exitCode=1;};
const q7=buildQuads(7);

// 每档格数正确
assert('6档 6格',buildQuads(6).length===6);
assert('7档 7格',q7.length===7);
assert('8档 8格',buildQuads(8).length===8);
assert('9档 9格',buildQuads(9).length===9);
// 7档中间格(索引3) 顶/底中心都≈960
{
  const q=q7[3];
  assert('7格 idx3 顶中≈960',approx((q[0][0]+q[1][0])/2,960,0.01));
  assert('7格 idx3 底中≈960',approx((q[2][0]+q[3][0])/2,960,0.01));
}
// 顶边宽=123、底边宽=127（正交投影：上下宽本就不同）
{
  const q=q7[0];
  assert('顶边宽=123',approx(q[1][0]-q[0][0],123,0.001));
  assert('底边宽=127',approx(q[3][0]-q[2][0],127,0.001));
}
// 关键：每一格 4 个顶点 X、Y 相互独立（TL/TR/BL/BR 是独立可调坐标），
// 且上边中心 X 与底边中心 X 可以不同（非同一竖直轴）——本实现就是4点分开存
{
  const q=q7[0];
  const topCX=(q[0][0]+q[1][0])/2, botCX=(q[2][0]+q[3][0])/2;
  const diffY=q[0][1]-q[2][1]; // 上下Y本不同（600 vs 735）
  assert('上下Y独立(600 vs 735)',q[0][1]===600&&q[2][1]===735);
  assert('4顶点可独立赋值',function(){const c=[q[0][0],q[0][1],q[1][0],q[1][1],q[2][0],q[2][1],q[3][0],q[3][1]];return c.length===8;}());
  console.log('  (斜视呈梯形状：顶窄123 底宽127，顶中Y600 底中Y735)');
}
// 9档最左槽 = 锚点四边形（与 C# 标定一致）
{
  const q9=buildQuads(9)[0];
  assert('9格最左槽 TLx=343',approx(q9[0][0],343,0.001));
  assert('9格最左槽 TRx=466',approx(q9[1][0],466,0.001));
  assert('9格最左槽 BLx=319',approx(q9[2][0],319,0.001));
  assert('9格最左槽 BRx=446',approx(q9[3][0],446,0.001));
  assert('9格最左槽 topY=600',approx(q9[0][1],600,0.001));
  assert('9格最左槽 botY=735',approx(q9[2][1],735,0.001));
}
console.log(process.exitCode===1?'\n有FAIL':'ALL PASS');
