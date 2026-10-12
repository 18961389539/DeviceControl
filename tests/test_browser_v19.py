"""Chromium offline V1.9: permission-safe backup panel and explicit unverified state."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  kw={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):kw['executable_path']=shutil.which('chromium')
  b=await pw.chromium.launch(**kw)
  p=await b.new_page(viewport={'width':1440,'height':980});errors=[]
  p.on('pageerror',lambda e:errors.append(str(e)))
  await p.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.9_Preview.html').read_text(encoding='utf-8'),wait_until='load')
  await p.locator('#loginUser').select_option('admin')
  await p.locator('#loginForm button').click()
  await p.locator('#loginOverlay').wait_for(state='hidden')
  await p.locator('[data-tab=templates]').click()
  assert await p.locator('#sandboxInspectBtn').is_visible()
  await p.locator('#sandboxInspectBtn').click()
  assert '请先' in await p.locator('#sandboxReport').inner_text()
  await p.locator('[data-template-action=capture]').click()
  await p.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Draft")')
  await p.locator('[data-template-action=validate]').click()
  await p.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Validated")')
  await p.locator('[data-template-action=publish]').click()
  await p.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Published")')
  await p.locator('#upgradeStation').select_option('B')
  await p.locator('[data-upgrade=preflight]').click()
  await p.wait_for_function('() => document.getElementById("upgradePreflight").textContent.includes("预检通过")')
  await p.locator('[data-upgrade=apply]').click()
  await p.wait_for_function('() => document.getElementById("upgradeHistory").options.length > 0 && document.getElementById("upgradeHistory").value.startsWith("dep-")')
  before=await p.locator('#upgradeEvidence').inner_text()
  await p.locator('#sandboxInspectBtn').click()
  await p.wait_for_function('() => document.getElementById("sandboxReport").textContent.includes("file-availability")')
  info=await p.locator('#sandboxReport').inner_text()
  assert '离线 HTML 没有真实 SQLite' in info and '未通过/未执行' in info and '生产数据写入：无' in info
  assert before==await p.locator('#upgradeEvidence').inner_text()
  await p.locator('#sandboxInspectBtn').scroll_into_view_if_needed()
  await p.screenshot(path=str(ROOT/'DeviceControl_V1.9_Sandbox.png'))
  assert not errors,errors
  await b.close()
  print('PASS V1.9 Chromium: history-selected sandbox, explicit missing database evidence, no deployment mutation')
if __name__=='__main__':asyncio.run(run())
