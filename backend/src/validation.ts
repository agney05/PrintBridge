export const allowedExtensions = new Set(['.pdf','.jpg','.jpeg','.png','.txt','.docx']);
export const isExpired = (now:number, deadline:number) => now >= deadline;
export const canCancel = (status:string) => status === 'queued';
export function matchesDocument(ext:string, mime:string|undefined, header:Buffer) {
  const expected:Record<string,string[]>={'.pdf':['application/pdf'],'.jpg':['image/jpeg'],'.jpeg':['image/jpeg'],'.png':['image/png'],'.docx':['application/zip','application/vnd.openxmlformats-officedocument.wordprocessingml.document']};
  if(ext==='.txt') return !header.subarray(0,4096).includes(0);
  if(!expected[ext]||!expected[ext].includes(mime||'')) return false;
  if(ext==='.docx') return header[0]===0x50&&header[1]===0x4b;
  return true;
}
