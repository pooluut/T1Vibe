using System;
class HookTests {
 static void Main(){bool down=false,trigger;int count=0;
 for(int i=0;i<3;i++){if(!Bridge.FilterApps(0x5d,0x100,ref down,out trigger)||trigger)throw new Exception("Apps down/repeat leaked");}
 if(!Bridge.FilterApps(0x5d,0x101,ref down,out trigger)||!trigger)throw new Exception("Apps up leaked or no activation");if(trigger)count++;
 if(Bridge.FilterApps(0x41,0x100,ref down,out trigger))throw new Exception("Normal key blocked");
 if(Bridge.FilterApps(0x1b,0x101,ref down,out trigger))throw new Exception("Escape blocked");
 if(!Bridge.FilterApps(0x5d,0x101,ref down,out trigger)||trigger)throw new Exception("Orphan up leaked/retriggered");
 if(count!=1)throw new Exception("Duplicate activation");
 Console.WriteLine("PASS: Apps down/repeat/up consumed; one activation on release; other keys pass through.");}
}
