using System;
class HookTests {
 static void Main(){bool down=false,trigger;int count=0;
 for(int i=0;i<3;i++){if(!Bridge.FilterApps(0x5d,0x100,ref down,out trigger)||trigger)throw new Exception("Apps down/repeat leaked");}
 if(!Bridge.FilterApps(0x5d,0x101,ref down,out trigger)||!trigger)throw new Exception("Apps up leaked or no activation");if(trigger)count++;
 if(Bridge.FilterApps(0x41,0x100,ref down,out trigger))throw new Exception("Normal key blocked");
 if(Bridge.FilterApps(0x1b,0x101,ref down,out trigger))throw new Exception("Escape blocked");
 if(!Bridge.FilterApps(0x5d,0x101,ref down,out trigger)||trigger)throw new Exception("Orphan up leaked/retriggered");
 if(count!=1)throw new Exception("Duplicate activation");
 var keys = new Bridge.NavigationKeys();
 foreach(uint key in new uint[]{0xa6,0xac,0x5d,0x26,0x28,0x0d}) {
  if(keys.Filter(key,0x100,false,out trigger))throw new Exception("Key intercepted outside ChatGPT");
  if(!keys.Filter(key,0x100,true,out trigger)||!trigger)throw new Exception("ChatGPT key not dispatched");
  if(!keys.Filter(key,0x100,true,out trigger)||trigger!=(key==0x26||key==0x28))throw new Exception("Incorrect repeat behavior");
  if(!keys.Filter(key,0x100,false,out trigger)||trigger)throw new Exception("Held key activated after focus loss");
  if(!keys.Filter(key,0x101,false,out trigger)||trigger)throw new Exception("Claimed release leaked after focus loss");
  if(keys.Filter(key,0x101,false,out trigger))throw new Exception("Unclaimed release intercepted");
 }
 foreach(uint key in new uint[]{0x25,0x27,0x41,0x5e,0x78})
  if(keys.Filter(key,0x100,true,out trigger))throw new Exception("Unmapped key intercepted");
 Console.WriteLine("PASS: navigation foreground gate, repeats, paired releases, focus loss and unmapped keys; Apps filter.");}
}
