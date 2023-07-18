﻿using System;
using System.Collections.Generic;
using System.IO;

namespace ET
{
    [EntitySystemOf(typeof(ActorInnerComponent))]
    [FriendOf(typeof(ActorInnerComponent))]
    public static partial class ActorInnerComponentSystem
    {
        [EntitySystem]
        private static void Destroy(this ActorInnerComponent self)
        {
            Fiber fiber = self.Fiber();
            ActorMessageQueue.Instance.RemoveQueue(fiber.Id);
        }

        [EntitySystem]
        private static void Awake(this ActorInnerComponent self)
        {
            Fiber fiber = self.Fiber();
            ActorMessageQueue.Instance.AddQueue(fiber.Id);
            fiber.ActorInnerComponent = self;
        }

        [EntitySystem]
        private static void Update(this ActorInnerComponent self)
        {
            self.list.Clear();
            Fiber fiber = self.Fiber();
            ActorMessageQueue.Instance.Fetch(fiber.Id, 1000, self.list);

            foreach (ActorMessageInfo actorMessageInfo in self.list)
            {
                self.HandleMessage(fiber, actorMessageInfo);
            }
        }

        private static void HandleMessage(this ActorInnerComponent self, Fiber fiber, in ActorMessageInfo actorMessageInfo)
        {
            if (actorMessageInfo.MessageObject is IActorResponse response)
            {
                self.HandleIActorResponse(response);
                return;
            }

            ActorId actorId = actorMessageInfo.ActorId;
            IActorMessage message = actorMessageInfo.MessageObject;

            MailBoxComponent mailBoxComponent = self.Fiber().Mailboxes.Get(actorId.InstanceId);
            if (mailBoxComponent == null)
            {
                Log.Warning($"actor not found mailbox, from: {actorId} current: {fiber.Address} {message}");
                if (message is IActorRequest request)
                {
                    IActorResponse resp = ActorHelper.CreateResponse(request, ErrorCore.ERR_NotFoundActor);
                    self.Reply(actorId.Address, resp);
                }
                return;
            }
            mailBoxComponent.Add(actorId.Address, message);
        }

        private static void HandleIActorResponse(this ActorInnerComponent self, IActorResponse response)
        {
            if (!self.requestCallback.Remove(response.RpcId, out ActorMessageSender actorMessageSender))
            {
                return;
            }
            Run(actorMessageSender, response);
        }
        
        private static void Run(ActorMessageSender self, IActorResponse response)
        {
            if (response.Error == ErrorCore.ERR_ActorTimeout)
            {
                self.Tcs.SetException(new Exception($"Rpc error: request, 注意Actor消息超时，请注意查看是否死锁或者没有reply: actorId: {self.ActorId} {self.Request}, response: {response}"));
                return;
            }

            if (self.NeedException && ErrorCore.IsRpcNeedThrowException(response.Error))
            {
                self.Tcs.SetException(new Exception($"Rpc error: actorId: {self.ActorId} request: {self.Request}, response: {response}"));
                return;
            }

            self.Tcs.SetResult(response);
        }
        
        public static void Reply(this ActorInnerComponent self, Address fromAddress, IActorResponse message)
        {
            self.SendInner(new ActorId(fromAddress, 0), message);
        }

        public static void Send(this ActorInnerComponent self, ActorId actorId, IActorMessage message)
        {
            self.SendInner(actorId, message);
        }

        private static void SendInner(this ActorInnerComponent self, ActorId actorId, IActorMessage message)
        {
            Fiber fiber = self.Fiber();
            
            // 如果发向同一个进程，则扔到消息队列中
            if (actorId.Process != fiber.Process)
            {
                throw new Exception($"actor inner process diff: {actorId.Process} {fiber.Process}");
            }

            if (actorId.Fiber == fiber.Id)
            {
                self.HandleMessage(fiber, new ActorMessageInfo() {ActorId = actorId, MessageObject = message});
                return;
            }
            
            ActorMessageQueue.Instance.Send(fiber.Address, actorId, message);
        }

        public static int GetRpcId(this ActorInnerComponent self)
        {
            return ++self.RpcId;
        }

        public static async ETTask<IActorResponse> Call(
                this ActorInnerComponent self,
                ActorId actorId,
                IActorRequest request,
                bool needException = true
        )
        {
            request.RpcId = self.GetRpcId();
            
            if (actorId == default)
            {
                throw new Exception($"actor id is 0: {request}");
            }

            return await self.Call(actorId, request.RpcId, request, needException);
        }
        
        public static async ETTask<IActorResponse> Call(
                this ActorInnerComponent self,
                ActorId actorId,
                int rpcId,
                IActorRequest iActorRequest,
                bool needException = true
        )
        {
            if (actorId == default)
            {
                throw new Exception($"actor id is 0: {iActorRequest}");
            }
            Fiber fiber = self.Fiber();
            if (fiber.Process != actorId.Process)
            {
                throw new Exception($"actor inner process diff: {actorId.Process} {fiber.Process}");
            }
            
            var tcs = ETTask<IActorResponse>.Create(true);

            self.requestCallback.Add(rpcId, new ActorMessageSender(actorId, iActorRequest, tcs, needException));
            
            self.SendInner(actorId, iActorRequest);

            
            async ETTask Timeout()
            {
                await fiber.TimerComponent.WaitAsync(ActorInnerComponent.TIMEOUT_TIME);

                if (!self.requestCallback.Remove(rpcId, out ActorMessageSender action))
                {
                    return;
                }
                
                if (needException)
                {
                    action.Tcs.SetException(new Exception($"actor sender timeout: {iActorRequest}"));
                }
                else
                {
                    IActorResponse response = ActorHelper.CreateResponse(iActorRequest, ErrorCore.ERR_Timeout);
                    action.Tcs.SetResult(response);
                }
            }
            
            Timeout().Coroutine();
            
            long beginTime = TimeInfo.Instance.ServerFrameTime();

            IActorResponse response = await tcs;
            
            long endTime = TimeInfo.Instance.ServerFrameTime();

            long costTime = endTime - beginTime;
            if (costTime > 200)
            {
                Log.Warning($"actor rpc time > 200: {costTime} {iActorRequest}");
            }
            
            return response;
        }
    }
}