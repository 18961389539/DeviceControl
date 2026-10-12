"""V0.5 offline browser smoke and debugger E2E, no real hardware."""
import asyncio
from pathlib import Path
from playwright.async_api import async_playwright
ROOT=Path(__file__).resolve().parents[1]
HTML=(ROOT/'tests'/'fixtures'/'DeviceControl_V0.5_Preview.html').read_text(encoding='utf8')
async def test():
 async with async_playwright() as pw:
  browser=await pw.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
  page=await browser.new_page(viewport={'width':1536,'height':980}, device_scale_factor=1)
  errors=[]
  page.on('pageerror',lambda e: errors.append(str(e)))
  await page.set_content(HTML,wait_until='load')
  await page.locator('#loginUser').select_option('engineer')
  await page.locator('#loginForm button[type="submit"]').click()
  await page.locator('#loginOverlay').wait_for(state='hidden')
  await page.locator('nav [data-tab="sequence"]').click()
  assert await page.locator('.flow-node').count()==9
  assert await page.locator('.flow-child').count()==4
  assert await page.locator('#flowVersion').inner_text()=='flow-v0.5.0'
  assert await page.locator('[data-debug="breakpoint"][data-index="2"]').is_enabled()
  await page.locator('[data-debug="breakpoint"][data-index="2"]').click()
  await page.wait_for_function('document.querySelectorAll(".break-dot.on").length===1')
  await page.screenshot(path=str(ROOT/'preview-flow-config.png'),full_page=True)
  await page.locator('nav [data-tab="manual"]').click()
  for axis in ('X','Y'):
   await page.locator('#axisSelect').select_option(axis)
   await page.locator('[data-action="servo-on"]').click()
   await page.wait_for_function('document.querySelector("#axisBadge").textContent==="已使能"')
   await page.locator('[data-action="axis-home"]').click()
   await page.wait_for_function('document.querySelector("#homedReadout").textContent==="已回零"')
  await page.locator('nav [data-tab="dashboard"]').click()
  await page.locator('[data-mode="Auto"]').click()
  await page.locator('[data-action="cycle-start"]').click()
  await page.locator('nav [data-tab="sequence"]').click()
  await page.wait_for_function('document.querySelector("#flowDebugStatus").textContent==="已暂停"',timeout=20000)
  await page.screenshot(path=str(ROOT/'preview-flow-paused.png'),full_page=True)
  assert await page.locator('[data-debug="step"]').is_enabled()
  await page.locator('[data-debug="step"]').click()
  await page.wait_for_function('document.querySelector("#flowDebugStatus").textContent==="已暂停" && document.querySelector(".flow-node.paused .flow-index")?.textContent.trim()==="04"',timeout=20000)
  assert await page.locator('.trace-row').count()>=3
  await page.locator('[data-debug="resume"]').click()
  await page.wait_for_function('document.querySelector("#cyclesValue").textContent==="1"',timeout=30000)
  await page.screenshot(path=str(ROOT/'preview-flow-trace.png'),full_page=True)
  await page.locator('nav [data-tab="analytics"]').click()
  assert await page.locator('.timing-row').count()==13
  assert not errors, errors
  await browser.close()
 print('PASS V0.5 browser: flow graph, breakpoint, pause, step, resume, parallel node traces, analytics')
if __name__=='__main__': asyncio.run(test())
