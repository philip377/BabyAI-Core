// Pure script unit harness: no browser, URLs, filesystem writes or network requests.
const fs=require('node:fs');
const vm=require('node:vm');
const assert=require('node:assert/strict');
const input=JSON.parse(fs.readFileSync(0,'utf8'));
const nodes=[];
class Element {
 constructor(tag){this.tag=tag;this.children=[];this.events={};this.textContent='';this.value='';this.disabled=false;nodes.push(this);}
 append(e){this.children.push(e);}
 addEventListener(name,fn){this.events[name]=fn;}
 click(){if(this.events.click)this.events.click();}
 remove(){}
 focus(){this.focused=true;}
}
const ids={};
for(const id of ['bundle','rows','download','error','saved','progress'])ids[id]=new Element('div');
ids.bundle.textContent=JSON.stringify(input.bundle);
let downloaded;
const context={document:{getElementById:id=>ids[id],createElement:tag=>new Element(tag),body:new Element('body')},
 Blob,URL:{createObjectURL:blob=>{downloaded=blob;return 'blob:unit-only';},revokeObjectURL:()=>{}},setTimeout:fn=>fn()};
vm.runInNewContext(input.script,context,{timeout:2000});
assert.equal(ids.download.disabled,true);
const selects=nodes.filter(n=>n.tag==='select');
const textareas=nodes.filter(n=>n.tag==='textarea');
assert.ok(selects.length>=2);
selects[1].value='suitable';selects[1].events.change();
assert.equal(ids.download.disabled,false);
ids.download.click();assert.ok(ids.error.textContent);assert.equal(downloaded,undefined);
assert.equal(textareas[1].focused,true);
textareas[1].value='UNIT TEST ONLY: same volume, review required';textareas[1].events.input();
ids.download.click();assert.ok(downloaded);assert.ok(ids.saved.textContent);
downloaded.text().then(text=>{const payload=JSON.parse(text);assert.equal(payload.entries[0].candidates[0].decision,'suitable');assert.equal('_input' in payload.entries[0].candidates[0],false);process.stdout.write(text);}).catch(e=>{process.stderr.write(String(e));process.exitCode=1;});
