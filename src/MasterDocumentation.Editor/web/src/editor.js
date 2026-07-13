import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Underline from '@tiptap/extension-underline'
import Highlight from '@tiptap/extension-highlight'
import { TextStyle } from '@tiptap/extension-text-style'
import Color from '@tiptap/extension-color'
import TextAlign from '@tiptap/extension-text-align'
import Link from '@tiptap/extension-link'
import Image from '@tiptap/extension-image'
import { Table, TableRow, TableHeader, TableCell } from '@tiptap/extension-table'
import TaskList from '@tiptap/extension-task-list'
import TaskItem from '@tiptap/extension-task-item'
import Subscript from '@tiptap/extension-subscript'
import Superscript from '@tiptap/extension-superscript'

const post = payload => window.chrome?.webview?.postMessage(payload)
const headings = editor => { const result=[]; editor.state.doc.descendants((node,pos)=>{if(node.type.name==='heading')result.push({level:node.attrs.level,text:node.textContent,pos})}); return result }
const emitContent = editor => post({type:'change',json:editor.getJSON(),html:editor.getHTML(),text:editor.getText(),headings:headings(editor)})
const editor = new Editor({
  element: document.querySelector('#editor'),
  extensions: [StarterKit, Underline, Highlight.configure({multicolor:true}), TextStyle, Color,
    TextAlign.configure({types:['heading','paragraph']}), Link.configure({openOnClick:false}), Image,
    Table.configure({resizable:true}), TableRow, TableHeader, TableCell, TaskList, TaskItem.configure({nested:true}), Subscript, Superscript],
  content: '<p></p>', autofocus: true,
  onUpdate: ({editor}) => emitContent(editor),
  onSelectionUpdate: ({editor}) => post({type:'selection',bold:editor.isActive('bold'),italic:editor.isActive('italic'),underline:editor.isActive('underline'),strike:editor.isActive('strike'),heading:editor.getAttributes('heading').level||0})
})

const commands = {
  bold:()=>editor.chain().focus().toggleBold().run(), italic:()=>editor.chain().focus().toggleItalic().run(), underline:()=>editor.chain().focus().toggleUnderline().run(), strike:()=>editor.chain().focus().toggleStrike().run(),
  bulletList:()=>editor.chain().focus().toggleBulletList().run(), orderedList:()=>editor.chain().focus().toggleOrderedList().run(), taskList:()=>editor.chain().focus().toggleTaskList().run(),
  blockquote:()=>editor.chain().focus().toggleBlockquote().run(), code:()=>editor.chain().focus().toggleCode().run(), codeBlock:()=>editor.chain().focus().toggleCodeBlock().run(),
  subscript:()=>editor.chain().focus().toggleSubscript().run(), superscript:()=>editor.chain().focus().toggleSuperscript().run(), clear:()=>editor.chain().focus().unsetAllMarks().clearNodes().run(),
  undo:()=>editor.chain().focus().undo().run(), redo:()=>editor.chain().focus().redo().run(), horizontalRule:()=>editor.chain().focus().setHorizontalRule().run(),
  alignLeft:()=>editor.chain().focus().setTextAlign('left').run(), alignCenter:()=>editor.chain().focus().setTextAlign('center').run(), alignRight:()=>editor.chain().focus().setTextAlign('right').run(), alignJustify:()=>editor.chain().focus().setTextAlign('justify').run(),
  heading:a=>editor.chain().focus().setHeading({level:Number(a.level)}).run(), paragraph:()=>editor.chain().focus().setParagraph().run(), color:a=>editor.chain().focus().setColor(a.color).run(), highlight:a=>editor.chain().focus().toggleHighlight({color:a.color}).run(),
  link:a=>editor.chain().focus().extendMarkRange('link').setLink({href:a.href}).run(), image:a=>editor.chain().focus().setImage({src:a.src,alt:a.alt||''}).run(), table:a=>editor.chain().focus().insertTable({rows:a.rows||3,cols:a.cols||3,withHeaderRow:true}).run(), gotoHeading:a=>editor.chain().focus().setTextSelection(Number(a.pos)+1).scrollIntoView().run()
}
window.chrome?.webview?.addEventListener('message', event => { const m=event.data; if(m.type==='setContent'){editor.commands.setContent(m.json||m.html||'<p></p>',false);editor.commands.focus('end');emitContent(editor)} else if(m.type==='command'&&commands[m.name])commands[m.name](m.args||{}) })
post({type:'ready'})
