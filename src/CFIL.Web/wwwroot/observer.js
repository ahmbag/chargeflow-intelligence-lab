// Drawing and hit testing only. Time, positions, decisions and money are supplied
// by typed .NET projections; this module never advances the simulation.
let receiver, last, hits=[], reconnectHandler, onlineHandler, offlineHandler, resizeHandler;
export function attach(dotnet) {
    receiver=dotnet;
    const status=()=>document.getElementById('connection-status');
    offlineHandler=()=>{if(status())status().hidden=false;};
    onlineHandler=async()=>{if(status())status().hidden=false;try{await receiver.invokeMethodAsync('Resynchronize');if(status())status().hidden=true;}catch{}};
    reconnectHandler=e=>{if(e.detail.state==='hide')onlineHandler();else offlineHandler();};
    window.addEventListener('offline',offlineHandler);window.addEventListener('online',onlineHandler);
    document.getElementById('components-reconnect-modal')?.addEventListener('components-reconnect-state-changed',reconnectHandler);
    resizeHandler=()=>{if(last)draw(...last);};window.addEventListener('resize',resizeHandler);
}
export function draw(network,scene,selected,park,route) {
    if(last && scene.generation<last[1].generation)return;
    last=[network,scene,selected,park,route];
    const canvas=document.getElementById('traffic-map');if(!canvas)return;
    const width=canvas.clientWidth,height=390,dpr=window.devicePixelRatio||1;
    canvas.width=width*dpr;canvas.height=height*dpr;
    const ctx=canvas.getContext('2d');ctx.scale(dpr,dpr);hits=[];
    const x=v=>32+(v-network.minX)/(network.maxX-network.minX)*(width-64);
    const y=v=>55+(network.maxY-v)/(network.maxY-network.minY)*(height-120);
    ctx.fillStyle='#101f2c';ctx.fillRect(0,0,width,height);
    ctx.strokeStyle='#203342';ctx.lineWidth=1;
    for(let gx=32;gx<width;gx+=60){ctx.beginPath();ctx.moveTo(gx,20);ctx.lineTo(gx,height-25);ctx.stroke();}
    for(const edge of network.edges){
        ctx.strokeStyle=route.includes(edge.id)?'#e6c46b':edge.internal?'#496175':'#7690a3';
        ctx.lineWidth=edge.lanes>1?7:2;ctx.beginPath();edge.shape.forEach((p,i)=>i?ctx.lineTo(x(p.x),y(p.y)):ctx.moveTo(x(p.x),y(p.y)));ctx.stroke();
        if(edge.lanes>1 && edge.shape.length>1){const p=edge.shape[Math.floor(edge.shape.length/2)];ctx.fillStyle='#e7edf1';ctx.font='14px sans-serif';ctx.fillText('→',x(p.x),y(p.y)-11);}
    }
    for(const p of scene.parks){
        const px=x(p.x),py=y(p.y);ctx.fillStyle=p.id===park?'#f7d478':'#7fe0c3';ctx.fillRect(px-13,py-14,26,28);
        ctx.fillStyle='#102b30';ctx.font='bold 18px sans-serif';ctx.fillText('ϟ',px-5,py+6);
        ctx.fillStyle='#e8f4f2';ctx.font='12px sans-serif';ctx.fillText(p.id,Math.max(3,px-28),py+45);
        hits.push({id:p.id,type:'SelectPark',x:px,y:py,r:22});
    }
    // Cars first, trucks last: large vehicles remain selectable in dense traffic.
    for(const v of [...scene.vehicles].sort((a,b)=>(a.kind==='Truck')-(b.kind==='Truck'))){
        if(['Scheduled','Arrived'].includes(v.status)||!v.edgeId||v.positionQuality==='Unknown edge; map position unavailable')continue;
        const px=x(v.x),py=y(v.y),truck=v.kind==='Truck',len=truck?17:7;
        // Heading follows the projected edge after the explicitly exaggerated
        // vertical scale, rather than treating screen pixels as physical metres.
        const angle=Math.atan2(-Math.sin(v.heading)*270/(network.maxY-network.minY),Math.cos(v.heading)*(width-64)/(network.maxX-network.minX));
        ctx.save();ctx.translate(px,py);ctx.rotate(angle);ctx.globalAlpha=v.stale?0.45:1;
        const stateColors={Stranded:'#ff7e78',Charging:'#77b9ff',Queued:'#f0be66',Connecting:'#c6a0ed',Departing:'#eef7f6',Incomplete:'#ef8fb3'};
        ctx.fillStyle=v.id===selected?'#ffd67a':stateColors[v.status]||(truck?'#76e1c3':'#9bb3d2');
        ctx.fillRect(-len,-(truck?4:2),len,truck?8:4); // front bumper anchor
        if(truck){ctx.fillStyle='#203342';ctx.fillRect(-4,-3,2,6);}
        if(v.id===selected){ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.strokeRect(-len-3,-7,len+6,14);}
        ctx.restore();hits.push({id:v.id,type:'SelectVehicle',x:px-Math.cos(angle)*len/2,y:py-Math.sin(angle)*len/2,r:truck?12:5});
    }
    ctx.fillStyle='#a9c1ce';ctx.font='12px sans-serif';ctx.fillText('0 km',26,height-14);ctx.fillText(`${Math.round(network.maxX/1000)} km`,width-65,height-14);
    canvas.onclick=event=>{const box=canvas.getBoundingClientRect(),px=event.clientX-box.left,py=event.clientY-box.top;
        const hit=[...hits].reverse().find(h=>Math.hypot(h.x-px,h.y-py)<=h.r);if(hit)receiver.invokeMethodAsync(hit.type,hit.id);};
}
export function detach(){window.removeEventListener('offline',offlineHandler);window.removeEventListener('online',onlineHandler);window.removeEventListener('resize',resizeHandler);document.getElementById('components-reconnect-modal')?.removeEventListener('components-reconnect-state-changed',reconnectHandler);receiver=null;last=null;hits=[];}
