import { Editor, Extension, Mark, Node, mergeAttributes } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Underline from '@tiptap/extension-underline'
import Highlight from '@tiptap/extension-highlight'
import { TextStyle, FontFamily, FontSize, LineHeight } from '@tiptap/extension-text-style'
import Color from '@tiptap/extension-color'
import TextAlign from '@tiptap/extension-text-align'
import Link from '@tiptap/extension-link'
import Image from '@tiptap/extension-image'
import { Table, TableRow, TableHeader, TableCell } from '@tiptap/extension-table'
import TaskList from '@tiptap/extension-task-list'
import TaskItem from '@tiptap/extension-task-item'
import Subscript from '@tiptap/extension-subscript'
import Superscript from '@tiptap/extension-superscript'
import CodeBlockLowlight from '@tiptap/extension-code-block-lowlight'
import { all, createLowlight } from 'lowlight'
import katex from 'katex'
import mermaid from 'mermaid'
import DOMPurify from 'dompurify'

const lowlight=createLowlight(all)
const codeLanguages=['csharp','javascript','typescript','xml','css','json','sql','powershell','bash','python','java','cpp','yaml']
const Spoiler=Mark.create({
  name:'spoiler',inclusive:true,
  parseHTML:()=>[{tag:'span[data-spoiler]'}],
  renderHTML:({HTMLAttributes})=>['span',mergeAttributes(HTMLAttributes,{'data-spoiler':'true',class:'spoiler'}),0],
  addCommands(){return{toggleSpoiler:()=>({commands})=>commands.toggleMark(this.name)}}
})
const SmartCodeBlock=CodeBlockLowlight.extend({
  addAttributes(){return{...this.parent?.(),autoDetect:{default:true,rendered:false}}}
}).configure({lowlight,defaultLanguage:null})
const SmartImage=Image.extend({
  addAttributes(){return{...this.parent?.(),width:{default:null,parseHTML:element=>Number.parseFloat(element.dataset.width||element.style.width||element.getAttribute('width'))||null},rotation:{default:0,parseHTML:element=>Number.parseInt(element.dataset.rotation||element.getAttribute('rotation')||'0',10)||0},align:{default:'center',parseHTML:element=>element.dataset.align||element.getAttribute('align')||'center'},wrap:{default:'none',parseHTML:element=>element.dataset.wrap||'none'},caption:{default:'',parseHTML:element=>element.dataset.caption||element.getAttribute('title')||''},cropTop:{default:0,parseHTML:element=>Number(element.dataset.cropTop)||0},cropRight:{default:0,parseHTML:element=>Number(element.dataset.cropRight)||0},cropBottom:{default:0,parseHTML:element=>Number(element.dataset.cropBottom)||0},cropLeft:{default:0,parseHTML:element=>Number(element.dataset.cropLeft)||0}}},
  renderHTML({HTMLAttributes}){const width=HTMLAttributes.width?Number(HTMLAttributes.width):null,rotation=Number(HTMLAttributes.rotation)||0,align=HTMLAttributes.align||'center',wrap=HTMLAttributes.wrap||'none',caption=String(HTMLAttributes.caption||''),crop=[HTMLAttributes.cropTop,HTMLAttributes.cropRight,HTMLAttributes.cropBottom,HTMLAttributes.cropLeft].map(value=>Math.max(0,Math.min(45,Number(value)||0)));delete HTMLAttributes.width;delete HTMLAttributes.rotation;delete HTMLAttributes.align;delete HTMLAttributes.wrap;delete HTMLAttributes.caption;delete HTMLAttributes.cropTop;delete HTMLAttributes.cropRight;delete HTMLAttributes.cropBottom;delete HTMLAttributes.cropLeft;const margin=wrap==='left'?'8px 18px 8px 0':wrap==='right'?'8px 0 8px 18px':align==='left'?'10px auto 10px 0':align==='right'?'10px 0 10px auto':'10px auto',float=wrap==='left'?'left':wrap==='right'?'right':'none',figureStyle=`display:block;max-width:100%;width:${width?`${width}%`:'fit-content'};margin:${margin};float:${float};`,imageStyle=`display:block;max-width:100%;width:100%;height:auto;transform:rotate(${rotation}deg);clip-path:inset(${crop[0]}% ${crop[1]}% ${crop[2]}% ${crop[3]}%);`;const image=['img',mergeAttributes(this.options.HTMLAttributes,HTMLAttributes,{'data-width':width||'','data-rotation':rotation,'data-align':align,'data-wrap':wrap,'data-caption':caption,'data-crop-top':crop[0],'data-crop-right':crop[1],'data-crop-bottom':crop[2],'data-crop-left':crop[3],title:caption||HTMLAttributes.alt||'',style:imageStyle})];return['figure',{'data-smart-image':'true','data-wrap':wrap,style:figureStyle},image,...(caption?[['figcaption',{},caption]]:[])]}
})
const cssNumber=value=>{const number=Number.parseFloat(value);return Number.isFinite(number)?number:null}
const AdvancedTextStyle=Extension.create({
  name:'advancedTextStyle',
  addGlobalAttributes(){return[{types:['textStyle'],attributes:{letterSpacing:{default:null,parseHTML:element=>cssNumber(element.style.letterSpacing),renderHTML:attributes=>attributes.letterSpacing===null?{}:{style:`letter-spacing:${attributes.letterSpacing}px`}}}}]}
})
const ParagraphLayout=Extension.create({
  name:'paragraphLayout',
  addGlobalAttributes(){return[{types:['paragraph','heading'],attributes:{
    spaceBefore:{default:null,parseHTML:element=>cssNumber(element.style.marginTop),renderHTML:attributes=>attributes.spaceBefore===null?{}:{style:`margin-top:${attributes.spaceBefore}px`}},
    spaceAfter:{default:null,parseHTML:element=>cssNumber(element.style.marginBottom),renderHTML:attributes=>attributes.spaceAfter===null?{}:{style:`margin-bottom:${attributes.spaceAfter}px`}},
    firstIndent:{default:null,parseHTML:element=>cssNumber(element.style.textIndent),renderHTML:attributes=>attributes.firstIndent===null?{}:{style:`text-indent:${attributes.firstIndent}px`}},
    leftIndent:{default:null,parseHTML:element=>cssNumber(element.style.marginLeft),renderHTML:attributes=>attributes.leftIndent===null?{}:{style:`margin-left:${attributes.leftIndent}px`}},
    rightIndent:{default:null,parseHTML:element=>cssNumber(element.style.marginRight),renderHTML:attributes=>attributes.rightIndent===null?{}:{style:`margin-right:${attributes.rightIndent}px`}},
    textDirection:{default:null,parseHTML:element=>element.getAttribute('dir')||null,renderHTML:attributes=>attributes.textDirection?{dir:attributes.textDirection,style:`direction:${attributes.textDirection}`}:{}}
  }}]}
})
const ListOptions=Extension.create({
  name:'listOptions',
  addGlobalAttributes(){return[{types:['bulletList','orderedList'],attributes:{listStyle:{default:null,parseHTML:element=>element.style.listStyleType||null,renderHTML:attributes=>attributes.listStyle?{style:`list-style-type:${attributes.listStyle}`}:{}}}}]}
})
const Callout=Node.create({
  name:'callout',group:'block',content:'block+',defining:true,
  addAttributes(){return{kind:{default:'info'},label:{default:'Примечание'}}},
  parseHTML:()=>[{tag:'div[data-callout]'}],
  renderHTML:({HTMLAttributes})=>['div',mergeAttributes(HTMLAttributes,{'data-callout':'true','data-kind':HTMLAttributes.kind,'data-label':HTMLAttributes.label,class:'callout'}),0]
})
const Collapsible=Node.create({
  name:'collapsible',group:'block',content:'block+',defining:true,
  addAttributes(){return{title:{default:'Подробнее'}}},
  parseHTML:()=>[{tag:'details[data-collapsible]',contentElement:'[data-details-content]'}],
  renderHTML:({HTMLAttributes})=>['details',mergeAttributes(HTMLAttributes,{'data-collapsible':'true',class:'collapsible',open:'open'}),['summary',{contenteditable:'false'},HTMLAttributes.title],['div',{'data-details-content':'true'},0]]
})
const PageBreak=Node.create({name:'pageBreak',group:'block',atom:true,parseHTML:()=>[{tag:'div[data-page-break]'}],renderHTML:()=>['div',{'data-page-break':'true',class:'page-break',contenteditable:'false'}]})
const Anchor=Node.create({
  name:'anchor',group:'inline',inline:true,atom:true,
  addAttributes(){return{name:{default:'anchor'}}},parseHTML:()=>[{tag:'span[data-document-anchor]'}],renderHTML:({HTMLAttributes})=>['span',mergeAttributes(HTMLAttributes,{'data-document-anchor':HTMLAttributes.name,id:HTMLAttributes.name,class:'document-anchor',contenteditable:'false'}),`⚓ ${HTMLAttributes.name}`]
})
const SafeHtml=Node.create({
  name:'safeHtml',group:'block',atom:true,
  addAttributes(){return{code:{default:'<p>HTML</p>'}}},parseHTML:()=>[{tag:'div[data-safe-html]'}],renderHTML:({HTMLAttributes})=>['div',mergeAttributes(HTMLAttributes,{'data-safe-html':'true','data-code':HTMLAttributes.code,class:'safe-html-node'}),HTMLAttributes.code],
  addNodeView(){return({node})=>{const dom=document.createElement('div');const draw=code=>{dom.className='safe-html-node';const sanitized=DOMPurify.sanitize(code,{USE_PROFILES:{html:true},FORBID_TAGS:['script','iframe','object','embed','form','input','button','link','style','video','audio','source','picture'],FORBID_ATTR:['style','srcset','poster','action','formaction','onerror','onload','onclick','onmouseover']});dom.innerHTML=sanitized;dom.querySelectorAll('[src]').forEach(element=>{const source=element.getAttribute('src')||'';if(!source.startsWith('https://assets.local/')&&!source.startsWith('data:image/'))element.removeAttribute('src')});dom.querySelectorAll('a').forEach(element=>{element.removeAttribute('target');element.setAttribute('rel','noopener noreferrer')})};draw(node.attrs.code);return{dom,update:updated=>{if(updated.type.name!=='safeHtml')return false;draw(updated.attrs.code);return true}}}}
})
const Formula=Node.create({
  name:'formula',group:'inline',inline:true,atom:true,
  addAttributes(){return{latex:{default:'x^2'}}},parseHTML:()=>[{tag:'span[data-formula]'}],renderHTML:({HTMLAttributes})=>['span',mergeAttributes(HTMLAttributes,{'data-formula':HTMLAttributes.latex,class:'formula-node'}),HTMLAttributes.latex],
  addNodeView(){return({node})=>{const dom=document.createElement('span');dom.className='formula-node';const draw=value=>{dom.dataset.formula=value;dom.title=value;try{katex.render(value,dom,{throwOnError:true,strict:false})}catch(error){dom.className='formula-node formula-error';dom.textContent=value;dom.title=String(error)}};draw(node.attrs.latex);return{dom,update:updated=>{if(updated.type.name!=='formula')return false;dom.className='formula-node';draw(updated.attrs.latex);return true}}}}
})
let mermaidSequence=0
let editorTheme='dark'
const configureMermaid=()=>mermaid.initialize({startOnLoad:false,theme:editorTheme,securityLevel:'strict',suppressErrorRendering:true})
configureMermaid()
const MermaidDiagram=Node.create({
  name:'mermaidDiagram',group:'block',atom:true,
  addAttributes(){return{code:{default:'flowchart LR\n  A --> B'}}},parseHTML:()=>[{tag:'div[data-mermaid]'}],renderHTML:({HTMLAttributes})=>['div',mergeAttributes(HTMLAttributes,{'data-mermaid':HTMLAttributes.code,class:'mermaid-node'}),HTMLAttributes.code],
  addNodeView(){return({node})=>{const dom=document.createElement('div');dom.className='mermaid-node';let revision=0;const draw=async code=>{const current=++revision;dom.textContent='Построение диаграммы…';dom.dataset.mermaid=code;try{const result=await mermaid.render(`md-mermaid-${++mermaidSequence}`,code);if(current===revision)dom.innerHTML=result.svg}catch(error){if(current===revision){dom.className='mermaid-node mermaid-error';dom.textContent=String(error)}}};draw(node.attrs.code);return{dom,update:updated=>{if(updated.type.name!=='mermaidDiagram')return false;dom.className='mermaid-node';draw(updated.attrs.code);return true}}}}
})

const post = payload => window.chrome?.webview?.postMessage(payload)
let currentDocumentId = 0
const headings = editor => {
  const result=[]
  editor.state.doc.descendants((node,pos)=>{
    if(node.type.name==='heading') {
      result.push({level:node.attrs.level,text:node.textContent,pos})
      return
    }
    if(node.type.name==='paragraph') {
      const markdown=node.textContent.match(/^(#{1,6})\s+(.+)$/)
      if(markdown) result.push({level:markdown[1].length,text:markdown[2],pos})
    }
  })
  return result
}
const documentText=editor=>editor.state.doc.textBetween(0,editor.state.doc.content.size,'\n',node=>node.type.name==='formula'?node.attrs.latex:node.type.name==='mermaidDiagram'?`Mermaid:\n${node.attrs.code}`:node.type.name==='safeHtml'?node.attrs.code:node.type.name==='anchor'?node.attrs.name:node.type.name==='pageBreak'?'\n':node.type.name==='image'?node.attrs.alt||'Изображение':'')
const contentPayload = (editor,type,extra={}) => ({type,documentId:currentDocumentId,json:editor.getJSON(),html:editor.getHTML(),text:documentText(editor),headings:headings(editor),...extra})
const emitContent = editor => post(contentPayload(editor,'change'))
let contentFrame=0
const scheduleContent = editor => {
  if(contentFrame)return
  contentFrame=requestAnimationFrame(()=>{contentFrame=0;emitContent(editor)})
}
let detectingCode=false
let copiedFormatting=null
const updateCodeLanguages=editor=>{
  if(detectingCode)return
  const updates=[]
  editor.state.doc.descendants((node,pos)=>{if(node.type.name==='codeBlock'&&node.attrs.autoDetect!==false&&node.textContent.trim()){const detected=lowlight.highlightAuto(node.textContent,{subset:codeLanguages}).data.language||null;if(detected&&detected!==node.attrs.language)updates.push({pos,node,language:detected})}})
  if(!updates.length)return
  detectingCode=true
  let transaction=editor.state.tr
  updates.forEach(x=>{transaction=transaction.setNodeMarkup(x.pos,undefined,{...x.node.attrs,language:x.language})})
  editor.view.dispatch(transaction);detectingCode=false
}
const WordLikeEditing=Extension.create({
  name:'wordLikeEditing',priority:1000,
  addKeyboardShortcuts(){return{
    Backspace:()=>{
      const{empty,$from}=this.editor.state.selection
      if(!empty||$from.parentOffset!==0)return false
      const block=$from.parent,alignment=block.attrs.textAlign
      if(block.type.name==='heading')return this.editor.chain().setParagraph().setTextAlign('left').run()
      if(block.type.name==='paragraph'&&alignment&&alignment!=='left')return this.editor.chain().setTextAlign('left').run()
      return false
    }
  }}
})
let updateContextUi=()=>{}
const editor = new Editor({
  element: document.querySelector('#editor'),
  extensions: [StarterKit.configure({codeBlock:false,heading:{levels:[1,2,3,4,5,6]}}), Underline, Highlight.configure({multicolor:true}), TextStyle,AdvancedTextStyle,ParagraphLayout,ListOptions, FontFamily, FontSize, LineHeight, Color,
    TextAlign.configure({types:['heading','paragraph']}), Link.configure({openOnClick:false,autolink:true,linkOnPaste:true,protocols:['masterdoc']}), SmartImage,
    SmartCodeBlock,Table.configure({resizable:true}), TableRow, TableHeader, TableCell, TaskList, TaskItem.configure({nested:true}), Subscript, Superscript,Spoiler,Callout,Collapsible,PageBreak,Anchor,SafeHtml,Formula,MermaidDiagram,WordLikeEditing],
  content: '<p></p>', autofocus: true,
  onUpdate: ({editor}) => {scheduleContent(editor);setTimeout(()=>updateCodeLanguages(editor),0);requestAnimationFrame(()=>updateContextUi(editor))},
  onSelectionUpdate: ({editor}) => {const selected=editor.state.selection.node?.type.name==='image'?editor.state.selection.node.attrs:null,block=editor.state.selection.$from.parent.attrs,textStyle=editor.getAttributes('textStyle');post({type:'selection',position:editor.state.selection.from,bold:editor.isActive('bold'),italic:editor.isActive('italic'),underline:editor.isActive('underline'),strike:editor.isActive('strike'),code:editor.isActive('code'),blockquote:editor.isActive('blockquote'),heading:editor.getAttributes('heading').level||0,fontFamily:textStyle.fontFamily||'',fontSize:textStyle.fontSize||'',letterSpacing:textStyle.letterSpacing??null,spaceBefore:block.spaceBefore??null,spaceAfter:block.spaceAfter??null,firstIndent:block.firstIndent??null,leftIndent:block.leftIndent??null,rightIndent:block.rightIndent??null,textDirection:block.textDirection||'ltr',imageSelected:Boolean(selected),imageSrc:selected?.src||'',imageAlt:selected?.alt||'',imageCaption:selected?.caption||'',imageWrap:selected?.wrap||'none',imageAlign:selected?.align||'center',imageRotation:Number(selected?.rotation)||0});requestAnimationFrame(()=>updateContextUi(editor))}
})

const createEditorPopup=(className,role,label)=>{
  const popup=document.createElement('div')
  popup.className=className
  popup.setAttribute('role',role)
  popup.setAttribute('aria-label',label)
  popup.hidden=true
  document.body.append(popup)
  return popup
}
const selectionToolbar=createEditorPopup('selection-toolbar','toolbar','Форматирование выделенного текста')
const slashMenu=createEditorPopup('slash-menu','listbox','Вставка блока')
const toolbarActions=[
  ['B','Полужирный (Ctrl+B)',()=>editor.chain().focus().toggleBold().run(),'bold'],
  ['I','Курсив (Ctrl+I)',()=>editor.chain().focus().toggleItalic().run(),'italic'],
  ['U','Подчёркивание (Ctrl+U)',()=>editor.chain().focus().toggleUnderline().run(),'underline'],
  ['S','Зачёркивание',()=>editor.chain().focus().toggleStrike().run(),'strike'],
  ['</>','Моноширинный текст',()=>editor.chain().focus().toggleCode().run(),'code']
]
toolbarActions.forEach(([label,title,action,mark])=>{
  const button=document.createElement('button')
  button.type='button'
  button.textContent=label
  button.title=title
  button.setAttribute('aria-label',title)
  button.addEventListener('mousedown',event=>{event.preventDefault();action();requestAnimationFrame(()=>updateContextUi(editor))})
  button.dataset.mark=mark
  selectionToolbar.append(button)
})
const slashActions=[
  {label:'Обычный текст',hint:'Абзац',keywords:'paragraph текст',run:()=>editor.chain().focus().setParagraph().run()},
  {label:'Заголовок 1',hint:'Крупный раздел',keywords:'heading h1 заголовок',run:()=>editor.chain().focus().setHeading({level:1}).run()},
  {label:'Заголовок 2',hint:'Подраздел',keywords:'heading h2 заголовок',run:()=>editor.chain().focus().setHeading({level:2}).run()},
  {label:'Маркированный список',hint:'Список с маркерами',keywords:'bullet list список',run:()=>editor.chain().focus().toggleBulletList().run()},
  {label:'Нумерованный список',hint:'Список с номерами',keywords:'number list список',run:()=>editor.chain().focus().toggleOrderedList().run()},
  {label:'Чек-лист',hint:'Список задач',keywords:'task checklist задачи',run:()=>editor.chain().focus().toggleTaskList().run()},
  {label:'Цитата',hint:'Выделенный блок',keywords:'quote цитата',run:()=>editor.chain().focus().toggleBlockquote().run()},
  {label:'Блок кода',hint:'Автоопределение языка',keywords:'code код',run:()=>editor.chain().focus().toggleCodeBlock({autoDetect:true}).run()},
  {label:'Таблица',hint:'3 × 3',keywords:'table таблица',run:()=>editor.chain().focus().insertTable({rows:3,cols:3,withHeaderRow:true}).run()},
  {label:'Примечание',hint:'Информационный блок',keywords:'callout note заметка',run:()=>editor.chain().focus().wrapIn('callout',{kind:'info',label:'Примечание'}).run()},
  {label:'Разделитель',hint:'Горизонтальная линия',keywords:'line rule линия',run:()=>editor.chain().focus().setHorizontalRule().run()}
]
let slashFiltered=[]
let slashIndex=0
const hideContextPopups=()=>{selectionToolbar.hidden=true;slashMenu.hidden=true}
const positionPopup=(popup,rect,above=false)=>{
  popup.hidden=false
  const bounds=popup.getBoundingClientRect()
  const left=Math.max(8,Math.min(window.innerWidth-bounds.width-8,rect.left+(rect.width-bounds.width)/2))
  const preferred=above?rect.top-bounds.height-8:rect.bottom+8
  const top=Math.max(8,Math.min(window.innerHeight-bounds.height-8,preferred))
  popup.style.left=`${left}px`
  popup.style.top=`${top}px`
}
const runSlashAction=item=>{
  const {$from}=editor.state.selection
  const start=$from.start()
  editor.chain().focus().deleteRange({from:start,to:$from.pos}).run()
  item.run()
  slashMenu.hidden=true
}
const renderSlashMenu=(query,rect)=>{
  const normalized=query.trim().toLocaleLowerCase()
  slashFiltered=slashActions.filter(item=>!normalized||`${item.label} ${item.hint} ${item.keywords}`.toLocaleLowerCase().includes(normalized)).slice(0,8)
  slashIndex=Math.min(slashIndex,Math.max(0,slashFiltered.length-1))
  slashMenu.replaceChildren()
  if(!slashFiltered.length){
    const empty=document.createElement('div')
    empty.className='slash-empty'
    empty.textContent='Команды не найдены'
    slashMenu.append(empty)
  }else slashFiltered.forEach((item,index)=>{
    const button=document.createElement('button')
    button.type='button'
    button.className=index===slashIndex?'selected':''
    button.setAttribute('role','option')
    button.setAttribute('aria-selected',String(index===slashIndex))
    button.innerHTML=`<span>${item.label}</span><small>${item.hint}</small>`
    button.addEventListener('mousedown',event=>{event.preventDefault();runSlashAction(item)})
    slashMenu.append(button)
  })
  positionPopup(slashMenu,rect)
}
updateContextUi=currentEditor=>{
  if(!currentEditor.isFocused){hideContextPopups();return}
  const {selection}=currentEditor.state
  const {$from,empty}=selection
  const parentText=$from.parent.textBetween(0,$from.parentOffset)
  const slash=empty&&$from.parent.type.name==='paragraph'?parentText.match(/^\/([^/\n]*)$/):null
  if(slash){
    selectionToolbar.hidden=true
    const caret=currentEditor.view.coordsAtPos($from.pos)
    renderSlashMenu(slash[1],{left:caret.left,right:caret.right,top:caret.top,bottom:caret.bottom,width:0,height:caret.bottom-caret.top})
    return
  }
  slashMenu.hidden=true
  if(empty||selection.node){selectionToolbar.hidden=true;return}
  const nativeSelection=window.getSelection()
  if(!nativeSelection?.rangeCount){selectionToolbar.hidden=true;return}
  const rect=nativeSelection.getRangeAt(0).getBoundingClientRect()
  if(!rect.width&&!rect.height){selectionToolbar.hidden=true;return}
  selectionToolbar.querySelectorAll('button[data-mark]').forEach(button=>button.classList.toggle('active',currentEditor.isActive(button.dataset.mark)))
  positionPopup(selectionToolbar,rect,true)
}
editor.view.dom.addEventListener('keydown',event=>{
  if(slashMenu.hidden)return
  if(event.key==='ArrowDown'||event.key==='ArrowUp'){
    event.preventDefault()
    const delta=event.key==='ArrowDown'?1:-1
    slashIndex=(slashIndex+delta+slashFiltered.length)%Math.max(1,slashFiltered.length)
    requestAnimationFrame(()=>updateContextUi(editor))
  }else if(event.key==='Enter'&&slashFiltered.length){
    event.preventDefault()
    runSlashAction(slashFiltered[slashIndex])
  }else if(event.key==='Escape'){
    event.preventDefault()
    slashMenu.hidden=true
  }
})
editor.view.dom.addEventListener('blur',()=>setTimeout(()=>{if(!selectionToolbar.matches(':hover')&&!slashMenu.matches(':hover'))hideContextPopups()},120))
window.addEventListener('resize',()=>updateContextUi(editor))
window.addEventListener('scroll',()=>updateContextUi(editor),true)
const transferFiles=files=>Array.from(files||[]).forEach(file=>{const reader=new FileReader();reader.onload=()=>post({type:'fileData',documentId:currentDocumentId,name:file.name||`clipboard-${Date.now()}.png`,mime:file.type||'application/octet-stream',data:String(reader.result||'')});reader.readAsDataURL(file)})
editor.view.dom.addEventListener('paste',event=>{const files=event.clipboardData?.files;if(files?.length){event.preventDefault();transferFiles(files)}})
editor.view.dom.addEventListener('dragover',event=>{if(event.dataTransfer?.types?.includes('Files'))event.preventDefault()})
editor.view.dom.addEventListener('drop',event=>{const files=event.dataTransfer?.files;if(files?.length){event.preventDefault();transferFiles(files)}})

const finiteOrNull=value=>{if(value===null||value===undefined||value==='')return null;const number=Number(value);return Number.isFinite(number)?number:null}
const paragraphAttributes=args=>({spaceBefore:finiteOrNull(args.spaceBefore),spaceAfter:finiteOrNull(args.spaceAfter),firstIndent:finiteOrNull(args.firstIndent),leftIndent:finiteOrNull(args.leftIndent),rightIndent:finiteOrNull(args.rightIndent),textDirection:['ltr','rtl'].includes(args.textDirection)?args.textDirection:null})
const copyCurrentFormatting=()=>{const state=editor.state,marks=state.selection.$from.marks().filter(mark=>mark.type.name!=='link'&&mark.type.name!=='spoiler').map(mark=>({type:mark.type.name,attrs:{...mark.attrs}})),block=state.selection.$from.parent;copiedFormatting={marks,blockAttrs:{textAlign:block.attrs.textAlign??null,lineHeight:block.attrs.lineHeight??null,spaceBefore:block.attrs.spaceBefore??null,spaceAfter:block.attrs.spaceAfter??null,firstIndent:block.attrs.firstIndent??null,leftIndent:block.attrs.leftIndent??null,rightIndent:block.attrs.rightIndent??null,textDirection:block.attrs.textDirection??null}};return true}
const applyCopiedFormatting=()=>{if(!copiedFormatting)return false;const{state,view}=editor,{from,to}=state.selection;if(from===to)return false;let transaction=state.tr.removeMark(from,to);state.doc.nodesBetween(from,to,(node,pos)=>{if(node.isText){const start=Math.max(from,pos),end=Math.min(to,pos+node.nodeSize);for(const saved of copiedFormatting.marks){const type=state.schema.marks[saved.type];if(type&&start<end)transaction=transaction.addMark(start,end,type.create(saved.attrs))}}else if((node.type.name==='paragraph'||node.type.name==='heading')&&pos>=0){transaction=transaction.setNodeMarkup(pos,undefined,{...node.attrs,...copiedFormatting.blockAttrs})}});view.dispatch(transaction);view.focus();return true}
const commands = {
  focus:a=>editor.commands.focus(a.position==='start'?'start':'end'),
  bold:()=>editor.chain().focus().toggleBold().run(), italic:()=>editor.chain().focus().toggleItalic().run(), underline:()=>editor.chain().focus().toggleUnderline().run(), strike:()=>editor.chain().focus().toggleStrike().run(),
  bulletList:()=>editor.chain().focus().toggleBulletList().run(), orderedList:()=>editor.chain().focus().toggleOrderedList().run(), taskList:()=>editor.chain().focus().toggleTaskList().run(),
  indent:()=>editor.isActive('taskItem')?editor.chain().focus().sinkListItem('taskItem').run():editor.chain().focus().sinkListItem('listItem').run(),outdent:()=>editor.isActive('taskItem')?editor.chain().focus().liftListItem('taskItem').run():editor.chain().focus().liftListItem('listItem').run(),
  blockquote:()=>editor.chain().focus().toggleBlockquote().run(), code:()=>editor.chain().focus().toggleCode().run(), codeBlock:()=>editor.chain().focus().toggleCodeBlock({autoDetect:true}).run(), spoiler:()=>editor.chain().focus().toggleSpoiler().run(),
  subscript:()=>editor.chain().focus().toggleSubscript().run(), superscript:()=>editor.chain().focus().toggleSuperscript().run(), clear:()=>editor.chain().focus().unsetAllMarks().clearNodes().run(),
  undo:()=>editor.chain().focus().undo().run(), redo:()=>editor.chain().focus().redo().run(), horizontalRule:()=>editor.chain().focus().setHorizontalRule().run(),
  alignLeft:()=>editor.chain().focus().setTextAlign('left').run(), alignCenter:()=>editor.chain().focus().setTextAlign('center').run(), alignRight:()=>editor.chain().focus().setTextAlign('right').run(), alignJustify:()=>editor.chain().focus().setTextAlign('justify').run(),
  heading:a=>editor.chain().focus().setHeading({level:Number(a.level)}).run(), paragraph:()=>editor.chain().focus().setParagraph().run(), color:a=>editor.chain().focus().setColor(a.color).run(), highlight:a=>a.color==='transparent'?editor.chain().focus().unsetHighlight().run():editor.chain().focus().toggleHighlight({color:a.color}).run(),
  fontFamily:a=>editor.chain().focus().setFontFamily(a.family).run(),fontSize:a=>editor.chain().focus().setFontSize(`${a.size}px`).run(),lineHeight:a=>editor.chain().focus().setLineHeight(String(a.value)).run(),letterSpacing:a=>editor.chain().focus().setMark('textStyle',{letterSpacing:finiteOrNull(a.value)}).run(),
  paragraphLayout:a=>{const type=editor.state.selection.$from.parent.type.name;if(type!=='paragraph'&&type!=='heading')return false;return editor.chain().focus().updateAttributes(type,paragraphAttributes(a)).run()},
  textDirection:a=>{const type=editor.state.selection.$from.parent.type.name;if(type!=='paragraph'&&type!=='heading')return false;return editor.chain().focus().updateAttributes(type,{textDirection:a.direction==='rtl'?'rtl':'ltr'}).run()},
  copyFormat:()=>copyCurrentFormatting(),applyFormat:()=>applyCopiedFormatting(),
  changeCase:()=>{const{from,to}=editor.state.selection;if(from===to)return false;const ranges=[];editor.state.doc.nodesBetween(from,to,(node,pos)=>{if(node.isText){const start=Math.max(from,pos),end=Math.min(to,pos+node.nodeSize);if(start<end)ranges.push({start,end,text:editor.state.doc.textBetween(start,end)})}});const lower=ranges.some(x=>/[a-zа-яё]/u.test(x.text));let tr=editor.state.tr;ranges.reverse().forEach(x=>{const value=lower?x.text.toLocaleUpperCase():x.text.toLocaleLowerCase();tr=tr.insertText(value,x.start,x.end)});editor.view.dispatch(tr);return true},
  nonBreakingSpace:()=>editor.chain().focus().insertContent('\u00a0').run(),softBreak:()=>editor.chain().focus().setHardBreak().run(),
  insertDateTime:a=>editor.chain().focus().insertContent(a.value||new Intl.DateTimeFormat('ru-RU',{dateStyle:'medium',timeStyle:'short'}).format(new Date())).run(),
  anchor:a=>editor.chain().focus().insertContent({type:'anchor',attrs:{name:String(a.name||'anchor').trim().replace(/[^a-zA-Z0-9а-яА-ЯёЁ_-]+/g,'-')}}).run(),
  safeHtml:a=>editor.isActive('safeHtml')?editor.chain().focus().updateAttributes('safeHtml',{code:String(a.code||'')}).run():editor.chain().focus().insertContent({type:'safeHtml',attrs:{code:String(a.code||'')}}).run(),
  codeLanguage:a=>editor.chain().focus().updateAttributes('codeBlock',{language:a.language==='auto'?null:a.language,autoDetect:a.language==='auto'}).run(),
  listOptions:a=>{const ordered=a.kind!=='bullet',type=ordered?'orderedList':'bulletList',style=a.style||(ordered?'decimal':'disc'),start=Math.max(1,Number(a.start)||1);let chain=editor.chain().focus();if(!editor.isActive(type))chain=ordered?chain.toggleOrderedList():chain.toggleBulletList();return chain.updateAttributes(type,ordered?{listStyle:style,start}:{listStyle:style}).run()},
  callout:a=>editor.isActive('callout')&&editor.getAttributes('callout').kind===(a.kind||'info')?editor.chain().focus().lift('callout').run():editor.isActive('callout')?editor.chain().focus().updateAttributes('callout',{kind:a.kind||'info',label:a.label||'Примечание'}).run():editor.chain().focus().wrapIn('callout',{kind:a.kind||'info',label:a.label||'Примечание'}).run(),
  removeCallout:()=>editor.chain().focus().lift('callout').run(),collapsible:a=>editor.isActive('collapsible')?editor.chain().focus().lift('collapsible').run():editor.chain().focus().wrapIn('collapsible',{title:a.title||'Подробнее'}).run(),pageBreak:()=>editor.chain().focus().insertContent({type:'pageBreak'}).run(),
  formula:a=>editor.isActive('formula')?editor.chain().focus().updateAttributes('formula',{latex:a.latex||'x^2'}).run():editor.chain().focus().insertContent({type:'formula',attrs:{latex:a.latex||'x^2'}}).run(),
  mermaid:a=>editor.isActive('mermaidDiagram')?editor.chain().focus().updateAttributes('mermaidDiagram',{code:a.code||''}).run():editor.chain().focus().insertContent({type:'mermaidDiagram',attrs:{code:a.code||'flowchart LR\n  A --> B'}}).run(),
  addColumnBefore:()=>editor.chain().focus().addColumnBefore().run(),addColumnAfter:()=>editor.chain().focus().addColumnAfter().run(),deleteColumn:()=>editor.chain().focus().deleteColumn().run(),
  addRowBefore:()=>editor.chain().focus().addRowBefore().run(),addRowAfter:()=>editor.chain().focus().addRowAfter().run(),deleteRow:()=>editor.chain().focus().deleteRow().run(),
  mergeCells:()=>editor.chain().focus().mergeCells().run(),splitCell:()=>editor.chain().focus().splitCell().run(),toggleHeaderRow:()=>editor.chain().focus().toggleHeaderRow().run(),deleteTable:()=>editor.chain().focus().deleteTable().run(),
  link:a=>editor.chain().focus().extendMarkRange('link').setLink({href:a.href}).run(), image:a=>editor.chain().focus().setImage({src:a.src,alt:a.alt||''}).run(),
  imageScale:a=>{const{selection}=editor.state;if(selection.node?.type.name!=='image')return false;const current=Number(selection.node.attrs.width)||100;return editor.chain().focus().updateAttributes('image',{width:Math.max(10,Math.min(100,current*Number(a.factor||1)))}).run()},
  imageRotate:a=>{const node=editor.state.selection.node;if(node?.type.name!=='image')return false;const rotation=((Number(node.attrs.rotation)||0)+Number(a.delta||90)+360)%360;return editor.chain().focus().updateAttributes('image',{rotation}).run()},
  imageAlign:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{align:['left','center','right'].includes(a.align)?a.align:'center'}).run(),
  imageAlt:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{alt:String(a.alt||'')}).run(),
  imageReplace:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{src:a.src,alt:a.alt||editor.state.selection.node.attrs.alt||''}).run(),
  imageCaption:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{caption:String(a.caption||'')}).run(),
  imageWrap:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{wrap:['none','left','right'].includes(a.wrap)?a.wrap:'none'}).run(),
  imageCrop:a=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().updateAttributes('image',{cropTop:Math.max(0,Math.min(45,Number(a.top)||0)),cropRight:Math.max(0,Math.min(45,Number(a.right)||0)),cropBottom:Math.max(0,Math.min(45,Number(a.bottom)||0)),cropLeft:Math.max(0,Math.min(45,Number(a.left)||0))}).run(),
  imageCopy:async()=>{const node=editor.state.selection.node;if(node?.type.name!=='image')return false;try{const blob=await fetch(node.attrs.src).then(response=>response.blob());await navigator.clipboard.write([new ClipboardItem({[blob.type]:blob})]);return true}catch(error){post({type:'copyText',value:node.attrs.src||''});return false}},
  imageDelete:()=>editor.state.selection.node?.type.name==='image'&&editor.chain().focus().deleteSelection().run(),
  imageOpen:()=>{const node=editor.state.selection.node;if(node?.type.name!=='image')return false;post({type:'openAsset',src:node.attrs.src||''});return true},
  table:a=>editor.chain().focus().insertTable({rows:a.rows||3,cols:a.cols||3,withHeaderRow:true}).run(), gotoHeading:a=>editor.chain().focus().setTextSelection(Number(a.pos)+1).scrollIntoView().run(),
  gotoFragment:a=>{const fragment=decodeURIComponent(String(a.fragment||'')).toLocaleLowerCase();let position=null;editor.state.doc.descendants((node,pos)=>{if(position!==null)return false;if(node.type.name==='anchor'&&String(node.attrs.name||'').toLocaleLowerCase()===fragment)position=pos;else if(node.type.name==='heading'){const slug=node.textContent.trim().toLocaleLowerCase().replace(/[^a-zа-яё0-9]+/gu,'-').replace(/^-|-$/g,'');if(slug===fragment||node.textContent.trim().toLocaleLowerCase()===fragment)position=pos}return position===null});if(position===null)return false;return editor.chain().focus().setTextSelection(position+1).scrollIntoView().run()},setZoom:a=>{document.querySelector('main').style.zoom=String(Math.max(.5,Math.min(2,Number(a.value)||1)));return true},
  setTheme:a=>{editorTheme=a.theme==='light'?'light':'dark';document.documentElement.dataset.theme=editorTheme;configureMermaid();let transaction=editor.state.tr;editor.state.doc.descendants((node,pos)=>{if(node.type.name==='mermaidDiagram')transaction=transaction.setNodeMarkup(pos,undefined,{...node.attrs})});if(transaction.docChanged)editor.view.dispatch(transaction);return true},
  find:a=>window.find(String(a.query||''),Boolean(a.caseSensitive),Boolean(a.backward),true,false,false,false)
}
window.chrome?.webview?.addEventListener('message', event => {
  const m=event.data
  if(m.type==='setContent') {
    currentDocumentId=Number(m.documentId)||0
    if(contentFrame){cancelAnimationFrame(contentFrame);contentFrame=0}
    editor.commands.setContent(m.json||m.html||'<p></p>',{emitUpdate:false})
    if(m.focus){editor.view.dom.focus({preventScroll:true});editor.commands.focus(m.focusPosition==='start'?'start':'end')}
    post(contentPayload(editor,'loaded'))
  } else if(m.type==='getContent') {
    post(contentPayload(editor,'snapshot',{requestId:m.requestId}))
  } else if(m.type==='command'&&commands[m.name]) {
    commands[m.name](m.args||{})
    requestAnimationFrame(()=>emitContent(editor))
  }
})
post({type:'ready'})
document.addEventListener('click',event=>{const spoiler=event.target.closest?.('[data-spoiler]');if(spoiler){spoiler.classList.toggle('revealed');event.preventDefault();return}const link=event.target.closest?.('a[href]');if(link&&(event.ctrlKey||event.detail>1)){post({type:'openLink',href:link.getAttribute('href')||''});event.preventDefault()}})
