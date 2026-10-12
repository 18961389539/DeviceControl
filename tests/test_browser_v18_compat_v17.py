"""V1.7 Chromium offline maintenance window + diagnostics + explicit reconciliation."""
import asyncio,shutil
from datetime import datetime,timedelta
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  opts={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):opts['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**opts)
  page=await browser.new_page(viewport={'width':1600,'height':960});errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.8_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginUser').select_option('admin')
  await page.locator('#loginForm button').click();await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('[data-tab=templates]').click()
  await page.locator('[data-template-action=capture]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Draft')")
  await page.locator('[data-template-action=validate]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Validated')")
  await page.locator('[data-template-action=publish]').click()
  await page.wait_for_function("() => document.getElementById('templateSummary').textContent.includes('Published')")
  await page.locator('#rolloutIds').fill('A,B')
  # No automatic deployment; future window must prevent explicit advance too.
  await page.locator('#rolloutWindowStart').fill('2030-01-01T10:00')
  await page.locator('#rolloutWindowEnd').fill('2030-01-01T11:00')
  await page.locator('[data-rollout=create]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('PilotPending')")
  await page.locator('[data-rollout=advance]').click()
  await page.wait_for_function("() => document.getElementById('rolloutMessage').textContent.includes('维护窗口内')",timeout=7000)
  assert 'PilotPending' in await page.locator('#rolloutProgress').inner_text()
  await page.locator('[data-rollout=diagnose]').click()
  await page.wait_for_function("() => document.getElementById('rolloutDiagnostics').textContent.includes('维护窗口关闭')")
  await page.locator('[data-rollout=abort]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('Aborted')")
  # Current window: pilot B in auto mode must fail without mutating versions.
  now=datetime.now()
  await page.locator('#rolloutWindowStart').fill((now-timedelta(minutes=5)).strftime('%Y-%m-%dT%H:%M'))
  await page.locator('#rolloutWindowEnd').fill((now+timedelta(minutes=45)).strftime('%Y-%m-%dT%H:%M'))
  await page.locator('#rolloutPilot').select_option('B')
  await page.locator('[data-rollout=create]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('PilotPending')")
  await page.locator('#workspaceStation').select_option('B')
  await page.wait_for_function("() => document.getElementById('workspaceTag').textContent==='工作区 B'")
  await page.locator('[data-tab=dashboard]').click();await page.locator('[data-mode=Auto]').click()
  await page.wait_for_function("() => document.getElementById('modeValue').textContent==='自动'")
  await page.locator('[data-tab=templates]').click();await page.locator('[data-rollout=advance]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('Paused')",timeout=8000)
  await page.locator('[data-rollout=diagnose]').click()
  await page.wait_for_function("() => document.getElementById('rolloutDiagnostics').textContent.includes('不是空闲手动模式')")
  await page.locator('#rolloutReconcileNote').fill('完成核查')
  await page.locator('[data-rollout=reconcile]').click()
  await page.wait_for_function("() => document.getElementById('rolloutMessage').textContent.includes('8～500')")
  # Return B to manual and reconcile original baseline.
  await page.locator('[data-tab=dashboard]').click();await page.locator('[data-mode=Manual]').click()
  await page.wait_for_function("() => document.getElementById('modeValue').textContent==='手动'")
  await page.locator('[data-tab=templates]').click()
  await page.locator('#rolloutReconcileNote').fill('已核查单站部署日志、旧版本和报警情况')
  await page.locator('[data-rollout=reconcile]').click()
  await page.wait_for_function("() => document.getElementById('rolloutSteps').textContent.includes('Pending') && !document.getElementById('rolloutSteps').textContent.includes('Failed')",timeout=9000)
  assert 'Paused' in await page.locator('#rolloutProgress').inner_text(), 'reconcile must not auto-resume'
  await page.locator('[data-rollout=resume]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('PilotPending')")
  await page.locator('[data-rollout=advance]').click()
  await page.wait_for_function("() => document.getElementById('rolloutProgress').textContent.includes('AwaitingEvidence')",timeout=12000)
  await page.locator('[data-rollout=diagnose]').click()
  await page.wait_for_function("() => document.getElementById('rolloutDiagnostics').textContent.includes('版本与状态检查通过')",timeout=9000)
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.8_Maintenance_Compat.png'),full_page=True)
  assert not errors,errors
  await browser.close()
  print('PASS V1.7 Chromium: closed window blocked, no auto-deploy, diagnostics, failed-step manual reconcile, explicit resume/advance')
if __name__=='__main__':asyncio.run(run())
