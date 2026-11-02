"""Offline human review form. Produces annotations for explicit local import only."""
import argparse
import json
import secrets
from pathlib import Path
from .ginger_batch import batch_lock
from .ginger_filter_plan import _save
from .ginger_review_feedback import make_bundle

STYLE = """
:root{font:16px/1.5 system-ui,sans-serif;color:#182635;background:#edf2f6}body{margin:0}
header{background:#18374a;color:white;padding:24px max(20px,calc((100vw - 1100px)/2));}
header h1{margin:0;font-size:28px}header p{max-width:850px;color:#d3e5ed}
main{max-width:1100px;margin:auto;padding:20px}button,select,textarea{font:inherit}
.toolbar{display:flex;align-items:center;gap:12px;flex-wrap:wrap;position:sticky;top:0;background:#edf2f6;padding:14px 0;z-index:1}
button{background:#12655d;color:white;border:0;border-radius:8px;padding:12px 18px;cursor:pointer}
button:disabled{opacity:.5;cursor:default}select,textarea{border:1px solid #a3b3bf;border-radius:6px;padding:9px;background:white;color:#182635}
textarea{display:block;box-sizing:border-box;width:100%;min-height:78px;margin:6px 0 12px;resize:vertical}
section{background:white;border:1px solid #d3dce3;border-radius:12px;padding:22px;margin:0 0 20px}
h2{font-size:21px;margin:0 0 8px}h3{font-size:17px}article{border-top:1px solid #dce5ec;margin-top:20px;padding-top:10px}
.meta,.muted{color:#526676;font-size:14px}.warning{background:#fff4d7;border-left:4px solid #c48b20;padding:12px 16px}
label{display:block;margin:12px 0 5px;font-weight:600}ul{padding-left:24px}.difference{color:#813b13}
#error{color:#9a2525;font-weight:600}#progress{margin-left:auto}a{color:#105f62}
:focus-visible{outline:3px solid #e7a72e;outline-offset:3px}@media(max-width:650px){main{padding:12px}section{padding:15px}.toolbar{position:static}}
"""
SCRIPT = r"""
'use strict';
const bundle=JSON.parse(document.getElementById('bundle').textContent);
const draft={schema:1,bundle_id:bundle.bundle_id,entries:[]};
const root=document.getElementById('rows');
const download=document.getElementById('download');
const error=document.getElementById('error');
const inputs=[];
const rowControls=new Map(),candidateControls=new Map();
function node(tag,text,parent,cls){const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;if(parent)parent.append(e);return e;}
function hasChanges(entry){return entry.decision!=='unreviewed'||entry.note.trim()||entry.candidates.some(c=>c.decision!=='unreviewed'||c.reason.trim());}
function progress(){const count=draft.entries.filter(hasChanges).length;document.getElementById('progress').textContent=`С пояснениями: ${count} из ${draft.entries.length}`;download.disabled=count===0;}
function selector(parent,label,options,onChange){const id='control-'+inputs.length;const l=node('label',label,parent);l.htmlFor=id;const s=node('select',undefined,parent);s.id=id;inputs.push(s);for(const [value,text]of options){const o=node('option',text,s);o.value=value;}s.addEventListener('change',()=>{onChange(s.value);progress();});return s;}
function note(parent,label,onChange){const id='control-'+inputs.length;const l=node('label',label,parent);l.htmlFor=id;const t=node('textarea',undefined,parent);t.id=id;t.maxLength=2000;inputs.push(t);t.addEventListener('input',()=>{onChange(t.value);progress();});return t;}
function value(v){return v===null||v===undefined||v===''?'не указано':v===true?'да':v===false?'нет':String(v);}
for(const row of bundle.rows){
 const entry={row_key:row.row_key,decision:'unreviewed',note:'',candidates:[]};draft.entries.push(entry);
 const section=node('section',undefined,root);
 node('h2',`Строка ${row.position}: ${row.title}`,section);
 node('p',`Прайс ${row.document} · ${row.supplier} · Цена из прайса: ${row.price_text}`,section,'meta');
 node('p',`Сохранённых кандидатов: ${row.candidates.length}. Это наблюдение, а не новый поиск.`,section,'muted');
 const rowSelect=selector(section,'Что делать с этой строкой?', [['unreviewed','Ещё не смотрел'],['needs_more_search','Нужен другой поиск'],['skip','Пропустить'],['unsure','Не уверен']],v=>entry.decision=v);
 const rowNote=note(section,'Пояснение по строке (необязательно)',v=>entry.note=v);
 rowControls.set(row.row_key,{select:rowSelect,note:rowNote,entry});
 if(row.candidates.length===0)node('p','Пустой список не доказывает отсутствие товара. Создание карточки не предлагается.',section,'warning');
 for(const candidate of row.candidates){
  const item={candidate_key:candidate.candidate_key,decision:'unreviewed',reason:''};entry.candidates.push(item);
  const article=node('article',undefined,section);node('h3',`Кандидат ${candidate.position}: ${candidate.title}`,article);
  const list=node('ul',undefined,article);
  for(const [group,label]of [['differences','Различие'],['uncertainties','Неопределённость']])for(const d of candidate[group]){
   const details=('source'in d||'candidate'in d)?` (прайс: ${value(d.source)}; каталог: ${value(d.candidate)})`:'';
   node('li',label+': '+d.message+details,list,group==='differences'?'difference':'');
  }
  const reason=note(article,'Почему? Для «подходит» и «не подходит» пояснение обязательно.',v=>item.reason=v);
  const candidateSelect=selector(article,'Твоё мнение о кандидате', [['unreviewed','Ещё не смотрел'],['suitable','Подходит по моему мнению'],['unsuitable','Не подходит'],['unsure','Не уверен']],v=>{item.decision=v;reason.required=v==='suitable'||v==='unsuitable';});
  item._input=reason;
  candidateControls.set(candidate.candidate_key,{select:candidateSelect,reason,item,rowKey:row.row_key});
 }
}
download.addEventListener('click',()=>{
 error.textContent='';
 for(const entry of draft.entries)for(const candidate of entry.candidates){if((candidate.decision==='suitable'||candidate.decision==='unsuitable')&&!candidate.reason.trim()){
  error.textContent='Добавь причину выбранного ответа. Это поможет понять правило.';candidate._input.focus();return;
 }}
 const payload={schema:draft.schema,bundle_id:draft.bundle_id,entries:draft.entries.map(e=>({...e,candidates:e.candidates.map(({_input,...c})=>c)}))};
 const blob=new Blob([JSON.stringify(payload,null,2)],{type:'application/json;charset=utf-8'});
 if(blob.size>2000000){error.textContent='Файл слишком большой. Сократи пояснения или проверь меньше позиций за раз.';return;}
 const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;a.download='ginger-feedback-'+bundle.bundle_id.slice(0,12)+'.json';document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
 document.getElementById('saved').textContent='Файл с пояснениями подготовлен для скачивания. Пришли его в эту задачу для проверки и сохранения. В Ginger ничего не изменено.';
});
document.getElementById('restore').addEventListener('change',async event=>{
 error.textContent='';
 try{
  const file=event.target.files[0];if(!file)return;if(file.size>2000000)throw new Error('Файл слишком большой.');
  const loaded=JSON.parse(await file.text());
  const keys=(object,expected)=>object&&typeof object==='object'&&!Array.isArray(object)&&Object.keys(object).sort().join(',')===expected.slice().sort().join(',');
  const text=value=>typeof value==='string'&&value.length<=2000&&!/[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(value);
  if(!keys(loaded,['schema','bundle_id','entries'])||loaded.schema!==1||loaded.bundle_id!==bundle.bundle_id||!Array.isArray(loaded.entries)||loaded.entries.length>bundle.rows.length)throw new Error('Файл относится к другой проверке или имеет неверный формат.');
  const seen=new Set(),updates=[];
  for(const entry of loaded.entries){
   if(!keys(entry,['row_key','decision','note','candidates'])||!rowControls.has(entry.row_key)||seen.has(entry.row_key)||!['unreviewed','skip','unsure','needs_more_search'].includes(entry.decision)||!text(entry.note)||!Array.isArray(entry.candidates))throw new Error('Неверная строка в файле пояснений.');
   seen.add(entry.row_key);const selected=new Set();
   for(const item of entry.candidates){
    const control=candidateControls.get(item.candidate_key);
    if(!keys(item,['candidate_key','decision','reason'])||!control||control.rowKey!==entry.row_key||selected.has(item.candidate_key)||!['unreviewed','suitable','unsuitable','unsure'].includes(item.decision)||!text(item.reason)||(['suitable','unsuitable'].includes(item.decision)&&!item.reason.trim()))throw new Error('Неверный кандидат или отсутствует причина.');
    selected.add(item.candidate_key);
   }
   updates.push(entry);
  }
  for(const entry of updates){
   const control=rowControls.get(entry.row_key);control.entry.decision=entry.decision;control.entry.note=entry.note;control.select.value=entry.decision;control.note.value=entry.note;
   for(const item of entry.candidates){const c=candidateControls.get(item.candidate_key);c.item.decision=item.decision;c.item.reason=item.reason;c.select.value=item.decision;c.reason.value=item.reason;c.reason.required=['suitable','unsuitable'].includes(item.decision);}
  }
  progress();document.getElementById('saved').textContent='Пояснения восстановлены только в этой странице. В Ginger ничего не изменено.';
 }catch(problem){error.textContent='Не удалось восстановить пояснения: '+problem.message;}
});
progress();
"""


def render_form(bundle):
    nonce=secrets.token_hex(16)
    # Literal backslash-u is required; data cannot terminate the inert JSON script element.
    data=json.dumps(bundle,ensure_ascii=False).replace("&",chr(92)+"u0026").replace("<",chr(92)+"u003c").replace(">",chr(92)+"u003e")
    return f'''<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'none'; form-action 'none'; base-uri 'none'">
<title>UNIX — проверка кандидатов Ginger</title><style nonce="{nonce}">{STYLE}</style></head><body>
<header><h1>Проверка кандидатов Ginger</h1><p>Отметь, что подходит и почему. Пояснения относятся только к показанным позициям. Они не связывают товары и не становятся общим правилом автоматически.</p></header>
<main><div class="warning">Это отдельная локальная форма. Изменения остаются в открытой странице до скачивания файла. Закрытие страницы без сохранения потеряет введённые пояснения.</div>
<div class="toolbar"><button id="download" type="button" disabled>Скачать мои пояснения</button><span id="progress" aria-live="polite"></span></div>
<label for="restore">Продолжить с ранее скачанного файла пояснений</label><input id="restore" type="file" accept="application/json,.json">
<p id="error" role="alert"></p><p id="saved" aria-live="polite"></p><div id="rows"></div>
<p class="muted">Нет соединения с сервером Ginger. Подтверждение записи в этой форме отсутствует.</p></main>
<script id="bundle" type="application/json" nonce="{nonce}">{data}</script><script nonce="{nonce}">{SCRIPT}</script></body></html>'''


def export_form(row_directory, output_directory):
    bundle=make_bundle(row_directory)
    directory=Path(output_directory)
    directory.mkdir(parents=True,exist_ok=False)
    _save(directory/"bundle.json",bundle)
    with (directory/"index.html").open("x",encoding="utf-8") as stream: stream.write(render_form(bundle))
    return {"form":str((directory/"index.html").resolve()),"bundle":str((directory/"bundle.json").resolve()),"can_write":False}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory",type=Path,required=True)
    parser.add_argument("--output-directory",type=Path,required=True)
    args=parser.parse_args()
    with batch_lock(args.row_directory): result=export_form(args.row_directory,args.output_directory)
    print(json.dumps(result,ensure_ascii=False))


if __name__ == "__main__": main()
