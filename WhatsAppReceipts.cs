using Microsoft.Playwright;

namespace MarketWatcher;
public sealed partial class BrowserScanner
{
    private static async Task WaitForWhatsAppReceiptAsync(IPage page,string id,string caption,bool image)
    {
        for(var attempt=0;attempt<30;attempt++)
        {
            var receipt=await ReadWhatsAppReceiptAsync(page,id,caption,image);
            if(receipt.Confirmed)return;
            if(receipt.Ack<0)break;
            await page.WaitForTimeoutAsync(500);
        }
        throw new InvalidOperationException("WhatsApp-Status für Nachrichten-ID "+id+" unklar; keine automatische erneute Sendung.");
    }
    public async Task<bool> IsWhatsAppMessageConfirmedAsync(string phone,string id,string caption,bool image)
    {
        await StartAsync();var page=await _context!.NewPageAsync();
        try
        {
            await page.GotoAsync("https://web.whatsapp.com/send?phone="+phone,new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
            await page.Locator("footer [contenteditable='true']").Last.WaitForAsync(new(){State=WaitForSelectorState.Visible,Timeout=30000});
            return (await ReadWhatsAppReceiptAsync(page,id,caption,image)).Confirmed;
        }
        finally{await page.CloseAsync();}
    }
    private static Task<WhatsAppReceipt> ReadWhatsAppReceiptAsync(IPage page,string id,string caption,bool image)=>page.EvaluateAsync<WhatsAppReceipt>("""
    async a=>{
      const collection=window.require('WAWebCollections').Msg;
      let m=collection.get(a.id);
      if(!m&&collection.getMessagesById)m=(await collection.getMessagesById([a.id]))?.messages?.[0];
      if(!m)return {confirmed:false,ack:0};
      const normalize=s=>(s||'').replace(/\s+/g,' ').trim();
      const payload=a.image?m.caption:m.body;
      const correct=m.id?.fromMe&&(a.image?m.type==='image':m.type==='chat')&&normalize(payload)===normalize(a.caption);
      return {confirmed:!!correct&&m.ack>=1,ack:m.ack??0};
    }
    """,new{id,caption,image}).WaitAsync(TimeSpan.FromSeconds(20));
    private sealed class WhatsAppReceipt
    {
        public bool Confirmed {get;set;}
        public int Ack {get;set;}
    }
}
