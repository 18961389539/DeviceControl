"""Chromium: V1.8 recovery drill never mutates actual station or rollout data."""
import asyncio,shutil
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
async def run():
 async with async_playwright() as pw:
  options={'headless':True,'args':['--no-sandbox']}
  if shutil.which('chromium'):options['executable_path']=shutil.which('chromium')
  browser=await pw.chromium.launch(**options)
  page=await browser.new_page(viewport={'width':1500,'height':990});errors=[]
  page.on('pageerror',lambda e:errors.append(str(e)))
  await page.set_content((ROOT/'tests'/'fixtures'/'DeviceControl_V1.8_Preview.html').read_text(encoding='utf8'),wait_until='load')
  await page.locator('#loginUser').select_option('admin');await page.locator('#loginForm button').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('[data-tab=templates]').click()
  # Injected drill is available even before any deployment; it is fully isolated.
  for name,expect in [('crash-after-intent','Applying'),('split-versions','原始版本匹配 否'),('corrupt-backup','Mismatch')]:
   await page.locator('#recoveryScenario').select_option(name)
   await page.locator('[data-rollout=drill]').click()
   await page.wait_for_function('(v) => document.getElementById("rolloutRecovery").textContent.includes(v)',arg=expect)
   assert 'ManualReview' in await page.locator('#rolloutRecovery').inner_text()
   assert '原批次和工站保持不变' in await page.locator('#rolloutMessage').inner_text()
  await page.locator('[data-template-action=capture]').click()
  await page.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Draft")')
  await page.locator('[data-template-action=validate]').click()
  await page.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Validated")')
  await page.locator('[data-template-action=publish]').click()
  await page.wait_for_function('() => document.getElementById("templateSummary").textContent.includes("Published")')
  await page.locator('[data-rollout=create]').click()
  await page.wait_for_function('() => document.getElementById("rolloutProgress").textContent.includes("PilotPending")')
  before=await page.locator('#rolloutProgress').inner_text()
  await page.locator('[data-rollout=recovery-report]').click()
  await page.wait_for_function('() => document.getElementById("rolloutRecovery").textContent.includes("NotStarted")')
  assert before == await page.locator('#rolloutProgress').inner_text()
  await page.locator('#recoveryScenario').select_option('split-versions')
  await page.locator('[data-rollout=drill]').click()
  await page.wait_for_function('() => document.getElementById("rolloutRecovery").textContent.includes("split-versions")')
  assert before == await page.locator('#rolloutProgress').inner_text()
  await page.locator('[data-rollout=recovery-report]').click()
  await page.wait_for_function('() => document.getElementById("rolloutRecovery").textContent.includes("NotStarted")')
  await page.screenshot(path=str(ROOT/'DeviceControl_V1.8_Recovery.png'),full_page=True)
  assert not errors,errors
  await browser.close()
  print('PASS V1.8 Chromium: isolated drills, read-only report, created rollout remains unchanged')
if __name__=='__main__':asyncio.run(run())
