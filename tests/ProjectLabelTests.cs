using System;
class ProjectLabelTests {
 static void Main(){string[] yes={"在 T1Vibe 中开始新聊天","在 ExampleProject 中开始新聊天","新建对话","New chat","Start new chat in T1Vibe"};foreach(string name in yes)if(!Bridge.IsNewConversationLabel(name)){Console.WriteLine("FAIL missing label: "+name);Environment.Exit(1);};string[] no={"T1Vibe 的项目操作","更改 T1Vibe 的标记","删除聊天","搜索聊天","T1Vibe"};foreach(string name in no)if(Bridge.IsNewConversationLabel(name)){Console.WriteLine("FAIL false match: "+name);Environment.Exit(1);};Console.WriteLine("PASS: observed project labels match; project menu, delete, search and title do not match.");}
}

